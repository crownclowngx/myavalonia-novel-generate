using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Infrastructure.Models;

/// <summary>只实现 DeepSeek 文本 SSE 协议。密钥设置在单条消息上，不能写入共享 DefaultRequestHeaders。</summary>
public sealed class DeepSeekTextModel(ConnectionService connections, HttpClient client) : ITextModel
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.Configuration.Connection.Settings.Provider != ModelProvider.DeepSeek) throw new InvalidOperationException("连接类型不匹配。");
        var secret = await connections.ReadSecretForRequestAsync(request.Configuration).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Configuration.Connection.Settings.AuthorizationEndpoint + "/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var system = request.SystemPrompt + (request.JsonOutput ? "\n只返回合法 JSON 对象。" + (request.Contract?.JsonSchema ?? "") : "");
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = request.Configuration.Preset.Model,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = request.UserPrompt } },
            max_tokens = request.Configuration.Preset.MaxOutputTokens,
            // 显式发送思考开关，避免 none 仍继承服务端默认思考；旧 medium 按官方兼容映射规范化为 high。
            thinking = new { type = request.Configuration.Preset.ReasoningEffort == "none" ? "disabled" : "enabled" },
            reasoning_effort = request.Configuration.Preset.ReasoningEffort == "medium" ? "high" : request.Configuration.Preset.ReasoningEffort,
            response_format = new { type = request.JsonOutput ? "json_object" : "text" },
            stream = true,
            stream_options = new { include_usage = true }
        }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ModelRequestException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelFailure.Authentication,
            HttpStatusCode.PaymentRequired => ModelFailure.Balance,
            HttpStatusCode.TooManyRequests => ModelFailure.RateLimit,
            _ => ModelFailure.Service
        }, "模型服务拒绝请求。");
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new ModelRequestException(ModelFailure.Protocol, "模型未返回 SSE 文本流。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return await ReadEventsAsync(reader, progress, cancellationToken).ConfigureAwait(false);
    }
    public static async Task<TextModelResponse> ReadEventsAsync(TextReader reader, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(); var data = new StringBuilder(); string? finish = null; var usage = new ModelUsage(null, null);
        var done = false; long total = 0; var lastReport = DateTimeOffset.MinValue;
        try
        {
            while (await BoundedLines.ReadAsync(reader, 262144, cancellationToken).ConfigureAwait(false) is { } line)
            {
                total += line.Length; if (total > 8000000) throw Protocol();
                if (line.StartsWith(':')) continue;
                if (line.Length > 0)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) { data.AppendLine(line[5..].TrimStart(' ')); if (data.Length > 262144) throw Protocol(); }
                    continue;
                }
                var payload = data.ToString().Trim(); data.Clear(); if (payload.Length == 0) continue;
                if (payload == "[DONE]") { done = true; break; }
                using var json = JsonDocument.Parse(payload); var root = json.RootElement;
                if (root.TryGetProperty("error", out _)) throw Protocol();
                if (root.TryGetProperty("usage", out var counts) && counts.ValueKind == JsonValueKind.Object)
                    usage = new(ReadCount(counts, "prompt_tokens"), ReadCount(counts, "completion_tokens"));
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw Protocol();
                if (choices.GetArrayLength() > 1) throw Protocol();
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) throw Protocol();
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null) finish = reason.GetString();
                    if (!choice.TryGetProperty("delta", out var delta)) continue;
                    if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null) throw Protocol();
                    // reasoning_content 只消耗有界协议缓冲，不进入正文、进度或账本。
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                    {
                        text.Append(content.GetString()); if (text.Length > 1000000) throw Protocol();
                        if (DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromMilliseconds(150)) { progress?.Report(text.ToString()); lastReport = DateTimeOffset.UtcNow; }
                    }
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { throw Protocol(); }
        finally { progress?.Report(text.ToString()); }
        // 思考可能耗尽整个输出额度，此时合法 length 事件没有可见正文。
        // 仍返回截断及服务端用量，让上层明确增加额度或调整思考设置；不能把已知用量丢成协议失败。
        if (!done || finish is not ("stop" or "length") || finish == "stop" && text.Length == 0) throw Protocol();
        return new(text.ToString(), finish == "length" ? ModelCompletion.Truncated : ModelCompletion.Complete, usage);
    }
    internal static long? ReadCount(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var count) || count.ValueKind == JsonValueKind.Null) return null;
        if (!count.TryGetInt64(out var number) || number < 0) throw Protocol(); return number;
    }
    private static ModelRequestException Protocol() => new(ModelFailure.Protocol, "模型文本流不完整或不受支持。");
}

/// <summary>逐字符读取利用 StreamReader 自身缓冲；先限制单行，再交给 JSON 解析，防止 ReadLine 先分配无界内存。</summary>
internal static class BoundedLines
{
    public static async Task<string?> ReadAsync(TextReader reader, int limit, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(); var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return builder.ToString().TrimEnd('\r');
            builder.Append(character[0]); if (builder.Length > limit) throw new ModelRequestException(ModelFailure.Protocol, "模型事件超过本地容量限制。");
        }
        return builder.Length == 0 ? null : builder.ToString().TrimEnd('\r');
    }
}
