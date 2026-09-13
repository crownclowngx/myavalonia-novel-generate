using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Application.Connections;
namespace NovelGeneratePlugin.Infrastructure.Models;

public interface ICodexProcess
{
    Task<int> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, string input, Action<string> onLine, CancellationToken cancellationToken);
}
/// <summary>不经过 PowerShell/cmd，逐个传入参数；取消或解析失败时终止本次创建的进程树并等待退出。</summary>
public sealed class CodexProcess : ICodexProcess
{
    public async Task<int> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, string input, Action<string> onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // 仅复用 CLI 自己管理的登录；父进程的 API Key 不能悄悄切换计费路径。
        start.Environment.Remove("CODEX_API_KEY"); start.Environment.Remove("OPENAI_API_KEY");
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested(); process.Start();
        void Stop() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        using var registration = cancellationToken.Register(Stop);
        // stderr 必须并发排空，既不阻塞子进程，也不把可能含敏感上下文的原文输出给 UI/日志。
        var errors = DrainErrorsAsync(process.StandardError, cancellationToken);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false); process.StandardInput.Close();
            long total = 0;
            while (await BoundedLines.ReadAsync(process.StandardOutput, 2000000, cancellationToken).ConfigureAwait(false) is { } line)
            {
                total += line.Length; if (total > 8000000) throw new ModelRequestException(ModelFailure.Protocol, "Codex 输出超过本地容量。");
                onLine(line);
            }
            await errors.ConfigureAwait(false); await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return process.ExitCode;
        }
        finally
        {
            Stop(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await errors.ConfigureAwait(false); } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
    private static async Task DrainErrorsAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { }
    }
}

/// <summary>
/// Codex 套餐适配层，只取 agent_message 和 usage。独立临时工作目录、忽略个人配置、关闭扩展与执行能力；
/// 未知工具事件立即终止。CLI 更新可能改变能力，验收版本及限制必须与专项文档一起维护。
/// </summary>
public sealed class CodexTextModel(ICodexProcess process) : ITextModel
{
    public static IReadOnlyList<string> DisabledFeatures { get; } = Array.AsReadOnly(new[]
    { "hooks", "plugins", "apps", "shell_tool", "unified_exec", "shell_snapshot", "multi_agent", "multi_agent_v2", "goals", "memories",
        "browser_use", "browser_use_external", "in_app_browser", "computer_use", "image_generation", "view_image", "code_mode", "code_mode_host",
        "skill_search", "skill_mcp_dependency_install", "workspace_dependencies", "remote_plugin", "sleep_tool", "tool_suggest" });
    public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        request.Validate(); if (request.Configuration.Connection.Settings.Provider != ModelProvider.CodexCli) throw new InvalidOperationException("连接类型不匹配。");
        var directory = Path.Combine(Path.GetTempPath(), "NovelGenerateModel", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var arguments = new List<string> { "exec", "--ignore-user-config", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--json",
                "-m", request.EffectivePreset.Model, "-c", "approval_policy=\"never\"", "-c", "web_search=\"disabled\"",
                "-c", "model_reasoning_effort=" + JsonSerializer.Serialize(request.EffectivePreset.ReasoningEffort), "-c", "project_doc_max_bytes=0" };
            foreach (var feature in DisabledFeatures) { arguments.Add("--disable"); arguments.Add(feature); }
            if (request.JsonOutput)
            {
                // 任意 JSON 对象不能使用 additionalProperties:false 的空 Schema；无任务契约时通过提示词和本地解析校验。
                if (request.Contract is { } contract)
                {
                    var schema = Path.Combine(directory, "output-schema.json"); await File.WriteAllTextAsync(schema, contract.JsonSchema, cancellationToken).ConfigureAwait(false);
                    arguments.Add("--output-schema"); arguments.Add(schema);
                }
            }
            arguments.Add("-");
            var text = ""; var usage = new ModelUsage(null, null); var completed = false; var messages = 0; var overLocalLimit = false;
            var prompt = "你是只返回文本的小说处理器。仅使用以下提供的内容；不要调用工具、读取文件、联网或执行命令。不要汇报过程。" +
                $"最终输出目标上限 {request.EffectivePreset.MaxOutputTokens} tokens。\n" + request.SystemPrompt +
                (request.JsonOutput ? "\n最终只输出合法 JSON 对象。" : "") + "\n\n任务内容：\n" + request.UserPrompt;
            var code = await process.RunAsync(request.Configuration.Connection.Settings.CodexExecutable, directory, arguments, prompt, line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                using var json = JsonDocument.Parse(line); var root = json.RootElement; var type = root.GetProperty("type").GetString();
                if (type is "error" or "turn.failed") throw new ModelRequestException(ModelFailure.Service, "Codex 未完成请求。");
                if (root.TryGetProperty("item", out var item))
                {
                    var kind = item.GetProperty("type").GetString();
                    // error 项是 CLI 启动诊断，不等同于 turn.failed；不回显原文，仍须完整正文和成功结束。
                    if (kind is not ("agent_message" or "reasoning" or "error")) throw new ModelRequestException(ModelFailure.Protocol, "Codex 返回了非文本工具事件，已停止请求。");
                    if (type == "item.completed" && kind == "agent_message")
                    {
                        // CLI 不提供稳定的正文 token delta。本阶段只显示最终消息，不能把思考或过程消息拼成小说。
                        if (++messages > 1) throw new ModelRequestException(ModelFailure.Protocol, "Codex 返回多条正文消息，需人工复核。");
                        text = item.GetProperty("text").GetString() ?? "";
                        overLocalLimit = text.Length > Math.Min(1000000, request.EffectivePreset.MaxOutputTokens * 4L);
                        // 软限制超出仍保存候选；绝对容量只保留有界前缀，最终标记截断而不是把候选丢掉。
                        if (text.Length > 1000000) text = text[..1000000];
                        progress?.Report(text);
                    }
                }
                if (type == "turn.completed")
                {
                    completed = true;
                    if (root.TryGetProperty("usage", out var counts)) usage = new(DeepSeekTextModel.ReadCount(counts, "input_tokens"), DeepSeekTextModel.ReadCount(counts, "output_tokens"));
                }
            }, cancellationToken).ConfigureAwait(false);
            if (code != 0 || !completed || string.IsNullOrWhiteSpace(text)) throw new ModelRequestException(ModelFailure.Service, "Codex 没有返回完整正文。");
            return new(text, overLocalLimit || usage.OutputTokens > request.EffectivePreset.MaxOutputTokens ? ModelCompletion.Truncated : ModelCompletion.Complete, usage);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

/// <summary>显式组合两个适配器，不通过服务定位器或反射发现未知 Provider。</summary>
public sealed class TextModelRouter(ConnectionService connections, DeepSeekTextModel deepSeek, CodexTextModel codex) : ITextModel
{
    public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await connections.ValidateCurrentAsync(request.Configuration).ConfigureAwait(false);
        return await (request.Configuration.Connection.Settings.Provider switch
        {
            ModelProvider.DeepSeek => deepSeek.GenerateAsync(request, progress, cancellationToken),
            ModelProvider.CodexCli => codex.GenerateAsync(request, progress, cancellationToken),
            _ => throw new NotSupportedException("不支持此模型连接类型。")
        }).ConfigureAwait(false);
    }
}
