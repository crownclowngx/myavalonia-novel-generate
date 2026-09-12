using System.Net;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Models;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Features.ModelConnections;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class ModelProtocolTests
{
    private static ModelPreset Preset => new("gpt-6-astra", 2048, "high");
    internal static TextModelRequest Request(bool json = false) => new(Guid.NewGuid(), new(Guid.NewGuid(),
        new(Guid.NewGuid(), 1, Guid.NewGuid(), new("测试", ModelProvider.CodexCli, "", @"C:\test\codex.exe", Preset, Preset, Preset)),
        ModelTask.Drafting, Preset), "只返回文本", "写雨夜书店", json);
    private static RequestBudget Budget(int requests = 3, long tokens = 100000) => new(Guid.NewGuid(), requests, tokens);
    private static string Event(string content, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content }, finish_reason = finish } } }) + "\n\n";
    private const string Usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":23,\"completion_tokens\":7}}\n\n";
    private const string Done = "data: [DONE]\n\n";
    private sealed class Capture : IProgress<string> { public string Text { get; private set; } = ""; public void Report(string value) => Text = value; }

    [Fact]
    public async Task SSE跨中文网络字节块空事件与推理分离()
    {
        var payload = ": keepalive\r\n\r\ndata: \r\n\r\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"秘密推理\"},\"finish_reason\":null}]}\n\n" +
            Event("雨落书窗") + Event("。", "stop") + Usage + Done;
        using var stream = new TinyChunks(Encoding.UTF8.GetBytes(payload)); using var reader = new StreamReader(stream, Encoding.UTF8);
        var progress = new Capture(); var result = await DeepSeekTextModel.ReadEventsAsync(reader, progress, default);
        Assert.Equal("雨落书窗。", result.Text); Assert.Equal(result.Text, progress.Text); Assert.Equal(new ModelUsage(23, 7), result.Usage);
    }
    [Theory]
    [InlineData("stop", ModelCompletion.Complete)]
    [InlineData("length", ModelCompletion.Truncated)]
    public async Task SSE完成与截断保留独立状态且未知用量为空(string finish, ModelCompletion expected)
    {
        var result = await DeepSeekTextModel.ReadEventsAsync(new StringReader(Event("正文", finish) + Done), null, default);
        Assert.Equal(expected, result.Completion); Assert.Null(result.Usage.InputTokens); Assert.Null(result.Usage.OutputTokens);
    }
    [Theory]
    [InlineData("")]
    [InlineData("data: bad-json\n\n")]
    [InlineData("data: {\"error\":{}}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"tool_calls\":[]}}]}\n\n")]
    public async Task SSE断流或非法事件不伪装完成并保留已接收片段(string suffix)
    {
        var capture = new Capture();
        await Assert.ThrowsAsync<ModelRequestException>(() => DeepSeekTextModel.ReadEventsAsync(new StringReader(Event("已收正文") + suffix), capture, default));
        Assert.Equal("已收正文", capture.Text);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"text\":\"\",\"count\":1}")]
    [InlineData("{\"text\":\"甲\",\"count\":-1}")]
    [InlineData("{\"text\":\"甲\",\"count\":1,\"extra\":true}")]
    [InlineData("{\"text\":\"甲\",\"text\":\"乙\",\"count\":1}")]
    public void 结构化空值越界多余与重复字段拒绝(string value) => Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(value, new SampleContract()));
    [Fact]
    public void 结构化合法对象通过() => ModelRequestService.ValidateJson("{\"text\":\"甲\",\"count\":1}", new SampleContract());
    private sealed class SampleContract : IModelOutputContract
    {
        public string JsonSchema => "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"text\",\"count\"],\"additionalProperties\":false}";
        public void Validate(JsonElement value)
        { if (value.EnumerateObject().Count() != 2 || string.IsNullOrWhiteSpace(value.GetProperty("text").GetString()) || value.GetProperty("count").GetInt32() is < 1 or > 3) throw new InvalidDataException(); }
    }

    [Fact]
    public async Task Schema计入预留且显示通知失败不改变成功终态()
    {
        await using var workspace = new TestWorkspace(); var store = new ModelRequestStore(workspace.Paths);
        var fake = new ScriptedTextModel(_ => new("正文", ModelCompletion.Complete, new(10, 20))); var service = new ModelRequestService(fake, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(Request(true) with { Contract = new LargeContract() }, Budget(tokens: 25000), null, default)); Assert.Empty(fake.Requests);
        var budget = Budget(); await service.GenerateAsync(Request(), budget, new BrokenDisplay(), default);
        Assert.Equal(RequestState.Completed, Assert.Single(store.List(budget.Id)).State);
    }
    private sealed class LargeContract : IModelOutputContract
    {
        public string JsonSchema => JsonSerializer.Serialize(new { type = "object", description = new string('甲', 10000) });
        public void Validate(JsonElement value) { }
    }
    private sealed class BrokenDisplay : IProgress<string> { public void Report(string value) => throw new InvalidOperationException("显示端失效"); }
    [Fact]
    public async Task Codex超过软上限仍保存完整候选并标记截断()
    {
        await using var workspace = new TestWorkspace(); var request = Request(); var text = new string('甲', request.Configuration.Preset.MaxOutputTokens * 4 + 1);
        var process = new FakeProcess([JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text } }), "{\"type\":\"turn.completed\"}"]);
        var store = new ModelRequestStore(workspace.Paths); var budget = Budget(); var service = new ModelRequestService(new CodexTextModel(process), store);
        var result = await service.GenerateAsync(request, budget, null, default); Assert.Equal(ModelCompletion.Truncated, result.Completion);
        Assert.Equal(text, Assert.Single(store.List(budget.Id)).PartialText);
        Assert.Equal(text, Assert.Single(store.Recent(request.Configuration.Connection.Id)).PartialText);
    }
    [Fact]
    public async Task 预算先落盘再调用且未知用量保留预留并防重复发送()
    {
        await using var workspace = new TestWorkspace(); var store = new ModelRequestStore(workspace.Paths); var budget = Budget(); var request = Request();
        var fake = new ScriptedTextModel(_ => { Assert.Equal(RequestState.Reserved, Assert.Single(store.List(budget.Id)).State); return new("正文", ModelCompletion.Complete, new(null, null)); });
        var service = new ModelRequestService(fake, store); await service.GenerateAsync(request, budget, null, default);
        var saved = Assert.Single(store.List(budget.Id)); Assert.Equal(saved.ReservedTokens, saved.ChargedTokens); Assert.Equal("正文", saved.PartialText);
        await Assert.ThrowsAnyAsync<Exception>(() => service.GenerateAsync(request, budget, null, default)); Assert.Single(fake.Requests);
    }
    [Fact]
    public async Task 预算不足和扩额被拒绝并且并发预留只允许一个()
    {
        await using var workspace = new TestWorkspace(); var store = new ModelRequestStore(workspace.Paths); var fake = new ScriptedTextModel(); var service = new ModelRequestService(fake, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(Request(), Budget(tokens: 1), null, default)); Assert.Empty(fake.Requests);
        var budget = Budget(); var request = Request();
        var entry = new ModelRequestEntry(request.OperationId, budget.Id, request.Configuration.BookId, request.Configuration.Connection.Id, 1, "gpt-6-astra", 20000, RequestState.Reserved, new(null, null), "", null, DateTimeOffset.UtcNow);
        var calls = Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => store.Reserve(entry with { Id = Guid.NewGuid() }, budget)))).ToArray();
        var results = await Task.WhenAll(calls); Assert.Single(results, e => e is null); Assert.Single(store.List(budget.Id));
        Assert.Throws<InvalidOperationException>(() => store.Reserve(entry, budget with { MaximumTokens = 200000 }));
    }
    [Fact]
    public async Task 失败后不重发且候选跨服务重建可恢复()
    {
        await using var workspace = new TestWorkspace(); var store = new ModelRequestStore(workspace.Paths); var budget = Budget();
        var model = new PartialFailure(); var service = new ModelRequestService(model, store);
        await Assert.ThrowsAsync<ModelRequestException>(() => service.GenerateAsync(Request(), budget, null, default));
        var recovered = Assert.Single(new ModelRequestStore(workspace.Paths).List(budget.Id));
        Assert.Equal("部分候选", recovered.PartialText); Assert.Equal(RequestState.Uncertain, recovered.State); Assert.Null(recovered.Usage.OutputTokens);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(Request(), budget, null, default)); Assert.Equal(1, model.Calls);
    }
    private sealed class PartialFailure : ITextModel
    {
        public int Calls;
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        { Calls++; progress?.Report("部分候选"); throw new IOException("不能回显的内部错误"); }
    }
    [Fact]
    public async Task 超时取消保留状态并区分原因()
    {
        await using var workspace = new TestWorkspace(); var store = new ModelRequestStore(workspace.Paths); var service = new ModelRequestService(new WaitingModel(), store);
        var budget = Budget(); var error = await Assert.ThrowsAsync<ModelRequestException>(() => service.GenerateAsync(Request(), budget, null, default, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(ModelFailure.Timeout, error.Failure); Assert.Equal("等待中的片段", Assert.Single(store.List(budget.Id)).PartialText);
        using var cancellation = new CancellationTokenSource(); budget = Budget(); var running = service.GenerateAsync(Request(), budget, null, cancellation.Token);
        await Task.Delay(30); cancellation.Cancel(); error = await Assert.ThrowsAsync<ModelRequestException>(() => running); Assert.Equal(ModelFailure.Cancelled, error.Failure);
    }
    private sealed class WaitingModel : ITextModel
    {
        public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        { progress?.Report("等待中的片段"); await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
    [Theory]
    [InlineData(401, ModelFailure.Authentication)]
    [InlineData(402, ModelFailure.Balance)]
    [InlineData(429, ModelFailure.RateLimit)]
    [InlineData(302, ModelFailure.Service)]
    public async Task HTTP认证余额限流与重定向停止且原始错误不泄露(int code, ModelFailure expected)
    {
        await using var workspace = new TestWorkspace(); var request = await ApiRequest(workspace, "https://example.test");
        var handler = new FakeHttp(_ => new((HttpStatusCode)code) { Content = new StringContent("unit-secret-never-echo") }); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => new DeepSeekTextModel(workspace.Connections, client).GenerateAsync(request, null, default));
        Assert.Equal(expected, error.Failure); Assert.DoesNotContain("unit-secret", error.Message); Assert.Equal(1, handler.Calls);
        using var production = DeepSeekTextModel.CreateClient(); // 正式组合使用禁用重定向的工厂，不把客户端默认配置藏在 VM。
    }
    [Fact]
    public async Task 双连接认证独立且协议没有工具字段()
    {
        await using var workspace = new TestWorkspace(); var a = await ApiRequest(workspace, "https://a.example.test"); var b = await ApiRequest(workspace, "https://b.example.test");
        var keys = new Dictionary<string, string>(); var handler = new FakeHttp(message =>
        {
            keys[message.RequestUri!.Host] = message.Headers.Authorization!.Parameter!;
            using var body = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()); Assert.False(body.RootElement.TryGetProperty("tools", out _));
            Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            return Sse(Event("完成", "stop") + Done);
        }); using var client = new HttpClient(handler); var model = new DeepSeekTextModel(workspace.Connections, client);
        await Task.WhenAll(model.GenerateAsync(a, null, default), model.GenerateAsync(b, null, default));
        Assert.Equal("unit-a.example.test", keys["a.example.test"]); Assert.Equal("unit-b.example.test", keys["b.example.test"]); Assert.Null(client.DefaultRequestHeaders.Authorization);
    }
    private static async Task<TextModelRequest> ApiRequest(TestWorkspace workspace, string endpoint)
    {
        var preset = new ModelPreset("deepseek-flash", 2048, "high");
        var connection = await workspace.Connections.SaveAsync(null, new("API", ModelProvider.DeepSeek, endpoint, "", preset, preset, preset));
        await workspace.Connections.SetSecretAsync(ConnectionService.Bind(connection), "unit-" + new Uri(endpoint).Host, false);
        return new(Guid.NewGuid(), new(Guid.NewGuid(), connection, ModelTask.Drafting, preset), "只返回文本", "写一句小说", false);
    }
    private static HttpResponseMessage Sse(string data)
    { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data) }; response.Content.Headers.ContentType = new("text/event-stream"); return response; }
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Interlocked.Increment(ref Calls); return Task.FromResult(respond(request)); } }
    private sealed class TinyChunks(byte[] bytes) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken); }

    [Fact]
    public async Task Codex参数不经过Shell禁用扩展且解析正文用量()
    {
        var process = new FakeProcess(["{\"type\":\"item.completed\",\"item\":{\"type\":\"error\",\"message\":\"启动诊断\"}}", "{\"type\":\"item.completed\",\"item\":{\"type\":\"reasoning\",\"text\":\"不能混入正文\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"书窗有雨\"}}",
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":30}}"]);
        var result = await new CodexTextModel(process).GenerateAsync(Request(), null, default);
        Assert.Equal("书窗有雨", result.Text); Assert.Equal(new ModelUsage(100, 30), result.Usage);
        Assert.Contains("--ignore-user-config", process.Arguments); Assert.Contains("shell_tool", process.Arguments); Assert.Contains("hooks", process.Arguments); Assert.Contains("read-only", process.Arguments);
        Assert.False(Directory.Exists(process.Directory));
    }
    [Theory]
    [InlineData("command_execution")]
    [InlineData("mcp_tool_call")]
    [InlineData("web_search")]
    public async Task Codex未知工具事件立即拒绝(string kind)
    {
        var process = new FakeProcess([JsonSerializer.Serialize(new { type = "item.started", item = new { type = kind } })]);
        var exception = await Assert.ThrowsAsync<ModelRequestException>(() => new CodexTextModel(process).GenerateAsync(Request(), null, default));
        Assert.Equal(ModelFailure.Protocol, exception.Failure); Assert.False(Directory.Exists(process.Directory));
    }
    private sealed class FakeProcess(string[] lines) : ICodexProcess
    {
        public IReadOnlyList<string> Arguments = []; public string Directory = "";
        public Task<int> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, string input, Action<string> onLine, CancellationToken cancellationToken)
        { Directory = directory; Arguments = arguments; foreach (var line in lines) onLine(line); return Task.FromResult(0); }
    }
    [Fact]
    public async Task 本机进程取消会等待退出且标准错误不会堵塞管道()
    {
        await using var workspace = new TestWorkspace(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var processId = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexProcess().RunAsync(executable, workspace.Root,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "1..3000 | ForEach-Object { [Console]::Error.WriteLine('x' * 100) }; [Console]::WriteLine($PID); Start-Sleep -Seconds 30"], "", line =>
            { processId = int.Parse(line); cancellation.Cancel(); }, cancellation.Token));
        Assert.True(processId > 0);
        try { using var stopped = System.Diagnostics.Process.GetProcessById(processId); Assert.True(stopped.HasExited); }
        catch (ArgumentException) { /* 进程已由操作系统回收。 */ }
    }
    [Fact]
    public async Task 预留后进程中断的账本重开仍阻止重复请求()
    {
        await using var workspace = new TestWorkspace(); var budget = Budget(); var request = Request(); var store = new ModelRequestStore(workspace.Paths);
        store.Reserve(new(request.OperationId, budget.Id, request.Configuration.BookId, request.Configuration.Connection.Id, 1,
            "gpt-6-astra", 20000, RequestState.Reserved, new(null, null), "", null, DateTimeOffset.UtcNow), budget);
        var fake = new ScriptedTextModel(); var reopened = new ModelRequestService(fake, new ModelRequestStore(workspace.Paths));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.GenerateAsync(Request(), budget, null, default)); Assert.Empty(fake.Requests);
    }
    [Fact]
    public async Task 界面启动不调用模型主动检测才发送且显示未知用量()
    {
        await using var workspace = new TestWorkspace(); var fake = new ScriptedTextModel(_ => new("雨落书窗。", ModelCompletion.Complete, new(null, null)));
        var request = Request(); await workspace.Connections.SaveAsync(null, request.Configuration.Connection.Settings);
        await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, new(fake, new ModelRequestStore(workspace.Paths)));
        await tool.InitializeAsync(); Assert.Empty(fake.Requests); tool.SelectedConnection = Assert.Single(tool.Connections); await tool.SaveBeforeCloseAsync();
        await tool.ProbeCommand.ExecuteAsync(null); Assert.Single(fake.Requests); Assert.Contains("已连通", tool.ProbeResult); Assert.Contains("未知", tool.ProbeResult);
    }
}
