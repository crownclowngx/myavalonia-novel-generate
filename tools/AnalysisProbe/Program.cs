using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Import;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Credentials;
using NovelGeneratePlugin.Infrastructure.Models;

// 显式开发工具，不纳入普通测试或生产包。输入与输出必须由调用者给出；本工具的 import/benchmark 不访问网络。
if (args.Length != 3 || args[0] is not ("import" or "benchmark" or "chunk" or "protocol" or "extract" or "retry-extract" or "revise-extract"))
    throw new ArgumentException("用法：AnalysisProbe <import|benchmark|chunk> <TXT路径或-> <新的输出目录>；chunk 从标准输入读取本次密钥");
var output = Path.GetFullPath(args[2]);
var resuming = args[0] is "retry-extract" or "revise-extract";
if (!resuming && (Directory.Exists(output) || File.Exists(output))) throw new InvalidOperationException("输出目录必须尚不存在，避免覆盖已有验证资料。");
if (resuming && !File.Exists(Path.Combine(output, "run-id.txt"))) throw new InvalidOperationException("恢复目录缺少运行身份。");
Directory.CreateDirectory(output);
var input = args[1];
if (args[0] is "chunk" or "protocol" or "extract" or "retry-extract" or "revise-extract")
{
    // 密钥通过标准输入传入，既不出现在进程命令行，也不写入配置文件。普通 import/benchmark 不读取凭据。
    var secret = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("标准输入缺少本次密钥。");
    var paths = new WorkspacePaths(output);
    using var vault = new UserCredentialVault(paths);
    var connections = new ConnectionService(new ConnectionStore(paths), vault);
    // 2026-09-12 本次密钥实际 /models 返回 deepseek-flash；不把公开文档中的历史别名假定为当前账户可用模型。
    var preset = new ModelPreset("deepseek-flash", args[0] == "protocol" ? 512 : 16384, args[0] == "protocol" ? "low" : "none");
    var connection = resuming ? (await connections.ListAsync()).Connections.Single() :
        await connections.SaveAsync(null, new("DeepSeek Flash 本次提取验证", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
    var binding = ConnectionService.Bind(connection);
    await connections.SetSecretAsync(binding, secret, false); secret = "";
    try
    {
        if (args[0] == "protocol")
        {
            using var diagnosticClient = new HttpClient(new ProtocolShapeHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })) { Timeout = Timeout.InfiniteTimeSpan };
            var diagnosticRequests = new ModelRequestService(new DeepSeekTextModel(connections, diagnosticClient), new ModelRequestStore(paths));
            var diagnosticBudget = new RequestBudget(Guid.NewGuid(), 1, 30000);
            try
            {
                var frozen = await connections.FreezeAsync(binding, Guid.NewGuid(), ModelTask.Checking);
                var response = await diagnosticRequests.GenerateAsync(new(Guid.NewGuid(), frozen, "只输出JSON对象。", "返回一个包含ok=true的JSON对象。", true), diagnosticBudget, null, default);
                Console.WriteLine(JsonSerializer.Serialize(new { State = "Completed", response.Usage }));
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(output, "usage.json"), JsonSerializer.Serialize(diagnosticRequests.List(diagnosticBudget.Id).Select(e => new { e.Id, e.Model, e.State, e.Usage, e.ReservedTokens, e.Failure })));
            }
            return;
        }
        var store = new ReferenceSourceStore(paths);
        var preview = resuming ? null : await new NovelImportService(new TxtSourceReader(), store).PreviewAsync(input, null, new(), default);
        if (preview is not null) store.Import(preview.Import);
        using var client = DeepSeekTextModel.CreateClient();
        var requests = new ModelRequestService(new DeepSeekTextModel(connections, client), new ModelRequestStore(paths));
        if (args[0] is "extract" or "retry-extract" or "revise-extract")
        {
            var runs = new AnalysisRunStore(paths);
            var runner = new NovelAnalysisRunService(store, runs, connections, requests, new NovelAnalysisNodePreparer());
            // 本次完整文件验证总额 80 次/300 万；先扣除 G0031 已消耗的 6 次及 200862 已知或保守预留 token。
            var run = resuming ? runs.Read(Guid.Parse(await File.ReadAllTextAsync(Path.Combine(output, "run-id.txt")))) :
                await runner.CreateAsync(preview!.Import.Book.Id, binding, 74, 2799138, new(32, 1200000), default);
            if (args[0] == "revise-extract") run = await runner.CreateAsync(run.BookId, binding, run.Budget.MaximumRequests, run.Budget.MaximumTokens, run.ReportReserve, default, previousRunId: run.Id);
            if (resuming) run = runner.Resume(run.Id, true, run.Budget.MaximumRequests, run.Budget.MaximumTokens);
            await File.WriteAllTextAsync(Path.Combine(output, "run-id.txt"), run.Id.ToString());
            try
            {
                var finished = await runner.ExecuteAsync(run.Id, new(), new RunProgress(), default);
                await File.WriteAllTextAsync(Path.Combine(output, "extraction-results.json"), JsonSerializer.Serialize(runner.ReadExtractions(run.Id), new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(new { finished.Id, State = finished.State.ToString(), finished.Message, Chunks = finished.Chunks.Length, Completed = finished.Nodes.Count(n => n.State == AnalysisNodeState.Completed) }));
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(output, "usage.json"), JsonSerializer.Serialize(runner.Usage(run.Id).Select(e => new { e.Id, e.Model, e.State, e.Usage, e.ReservedTokens, e.Failure }), new JsonSerializerOptions { WriteIndented = true }));
            }
            return;
        }
        var service = new NovelChunkAnalysisService(connections, requests);
        var budget = new RequestBudget(Guid.NewGuid(), 2, 150000);
        var chunk = preview!.Import.Chunks.Skip(1).FirstOrDefault() ?? preview.Import.Chunks[0];
        Console.WriteLine(JsonSerializer.Serialize(new { State = "RequestStarting", Model = preset.Model, Chunk = chunk.Number, BodyCharacters = chunk.Body.Length, budget.MaximumRequests, budget.MaximumTokens }));
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await service.AnalyzeAsync(preview.Import, chunk.Id, binding, Guid.NewGuid(), budget, default);
            await File.WriteAllTextAsync(Path.Combine(output, "chunk-analysis.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { State = "Completed", Entities = result.Entities.Length, Findings = result.Findings.Length, Gaps = result.Gaps.Length, Seconds = watch.Elapsed.TotalSeconds }));
        }
        finally
        {
            var entries = requests.List(budget.Id);
            await File.WriteAllTextAsync(Path.Combine(output, "usage.json"), JsonSerializer.Serialize(entries.Select(e => new { e.Id, e.Model, e.State, e.Usage, e.ReservedTokens, e.Failure }), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(entries.Select(e => new { e.State, e.Usage, e.Failure })));
        }
    }
    finally { await connections.ClearSecretAsync(binding); }
    return;
}
if (args[0] == "benchmark")
{
    input = Path.Combine(output, "synthetic-capacity.txt");
    var text = new StringBuilder();
    for (var chapter = 1; text.Length < AnalysisLimits.MaximumCharacters; chapter++)
        text.Append($"第{chapter}章 容量验证\r\n").Append(string.Concat(Enumerable.Repeat("雾港的邮差沿着长街寻找钟楼。\r\n", 100)));
    text.Length = AnalysisLimits.MaximumCharacters;
    await File.WriteAllTextAsync(input, text.ToString(), new UTF8Encoding(false));
}

var results = new List<object>();
for (var iteration = 0; iteration < (args[0] == "benchmark" ? 3 : 1); iteration++)
{
    var store = new ReferenceSourceStore(new WorkspacePaths(Path.Combine(output, "run-" + iteration)));
    var service = new NovelImportService(new TxtSourceReader(), store);
    var baselineMemory = GC.GetTotalMemory(true); var maximumMemory = baselineMemory;
    using var sampling = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                maximumMemory = Math.Max(maximumMemory, GC.GetTotalMemory(false));
                await Task.Delay(10, sampling.Token);
            }
        }
        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
    });
    var watch = Stopwatch.StartNew();
    var preview = await service.PreviewAsync(input, null, new(), default);
    var saved = await service.ImportAsync(preview, default);
    var importMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart(); var reopened = store.Read(saved.Id); var reopenMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart(); store.List(); var listMilliseconds = watch.Elapsed.TotalMilliseconds;
    sampling.Cancel(); await sampler;
    var result = new
    {
        Iteration = iteration, saved.Id, saved.Name, saved.Characters,
        Bytes = preview.Import.Source.Bytes.Length, preview.Import.Source.ByteHash,
        preview.Import.Source.EncodingName, Sections = reopened.Sections.Length, Chunks = reopened.Chunks.Length,
        CoveredCharacters = reopened.Chunks.Sum(c => c.Body.Length), preview.Warnings,
        ImportMilliseconds = importMilliseconds, ReopenMilliseconds = reopenMilliseconds, ListMilliseconds = listMilliseconds,
        AdditionalManagedMiB = (maximumMemory - baselineMemory) / 1048576d,
        Machine = Environment.MachineName, Processors = Environment.ProcessorCount, Runtime = Environment.Version.ToString(),
        SourceScope = args[0] == "benchmark" ? "synthetic capacity only" : "complete provided file; original novel coverage unverified"
    };
    results.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
await File.WriteAllTextAsync(Path.Combine(output, "import-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

/// <summary>只在显式协议探针中读取有限响应并显示字段形状，不保存请求头、推理正文或模型正文。</summary>
sealed class ProtocolShapeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Console.WriteLine(JsonSerializer.Serialize(new { HttpStatus = (int)response.StatusCode, ContentType = response.Content.Headers.ContentType?.ToString(), Bytes = bytes.Length }));
        var finishes = new List<string>(); var errors = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)))
        {
            var data = line[5..].Trim(); if (data is "[DONE]" or "") continue;
            using var frame = JsonDocument.Parse(data);
            if (frame.RootElement.TryGetProperty("error", out var error))
            {
                var safe = System.Text.RegularExpressions.Regex.Replace(error.GetRawText(), @"sk-[A-Za-z0-9_-]+", "[REDACTED]");
                errors.Add(safe[..Math.Min(safe.Length, 300)]);
            }
            if (frame.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String) finishes.Add(finish.GetString()!);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { FinishReasons = finishes, Errors = errors }));
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Take(12))
        {
            var data = line[5..].Trim(); if (data == "[DONE]") { Console.WriteLine("SSE_DONE"); continue; }
            using var document = JsonDocument.Parse(data); var root = document.RootElement;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Keys = root.EnumerateObject().Select(p => p.Name),
                ChoicesKind = root.TryGetProperty("choices", out var choices) ? choices.ValueKind.ToString() : "missing",
                Choices = choices.ValueKind == JsonValueKind.Array ? choices.EnumerateArray().Select(c => new
                {
                    Keys = c.EnumerateObject().Select(p => p.Name),
                    DeltaKeys = c.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object ? delta.EnumerateObject().Select(p => p.Name).ToArray() : []
                }).ToArray() : null
            }));
        }
        var replacement = new ByteArrayContent(bytes);
        foreach (var header in response.Content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content.Dispose(); response.Content = replacement;
        return response;
    }
}

sealed class RunProgress : IProgress<AnalysisRun>
{
    public void Report(AnalysisRun value) => Console.WriteLine(JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, State = value.State.ToString(), Completed = value.Nodes.Count(n => n.State == AnalysisNodeState.Completed), Total = value.Nodes.Length, value.Message }));
}
