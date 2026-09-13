using System.Text.Json;
using System.Text.Json.Nodes;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Models;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class AnalysisRecoveryTests
{
    internal static TextModelResponse Good() => new(NovelChunkAnalysisTests.Json(NovelChunkAnalysisTests.Output()), ModelCompletion.Complete, new(100, 200));
    internal sealed class Fixture(TestWorkspace workspace, ITextModel model)
    {
        public ReferenceSourceStore Sources { get; } = new(workspace.Paths);
        public AnalysisRunStore Runs { get; } = new(workspace.Paths);
        public ModelRequestService Requests { get; } = new(model, new ModelRequestStore(workspace.Paths));
        public NovelAnalysisRunService Service => new(Sources, Runs, workspace.Connections, Requests, new NovelAnalysisNodePreparer());
        public async Task<AnalysisRun> Create(int chunks = 1, int requests = 100)
        {
            var source = ReferenceSourceTests.Create(string.Concat(Enumerable.Range(1, chunks).Select(i => $"第{i}章\n林远走进城门。\n不要回头。\n")));
            Sources.Import(NovelTextPartitioner.Partition(source.Source, source.Book.Name, new(6000, 0), default));
            var preset = new ModelPreset("deepseek-flash", 65536, "high");
            var connection = await workspace.Connections.SaveAsync(null, new("恢复测试", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
            return await Service.CreateAsync(source.Book.Id, ConnectionService.Bind(connection), requests, 8000000, new(1, 10000), default);
        }
    }

    [Fact]
    public void 缺口可选证据验证段号并完整保留而任意字段仍拒绝()
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0];
        var contract = new ChunkAnalysisContract(input.Source, chunk, Guid.NewGuid(), "test", ChunkAnalysisContract.Passages(input.Source, chunk));
        var json = JsonNode.Parse(Good().Text)!;
        json["Gaps"]![0]!["Evidence"] = JsonNode.Parse("""[{"Passage":2}]""");
        Assert.Single(contract.Read(json.ToJsonString()).Gaps[0].Evidence);
        json["Gaps"]![0]!["Evidence"]![0]!["Passage"] = 999;
        Assert.Throws<ModelContractException>(() => contract.Read(json.ToJsonString()));
        json["Gaps"]![0]!["Evidence"]![0]!["Passage"] = 2;
        json["Gaps"]![0]!["Unexpected"] = "不能静默丢弃";
        Assert.Throws<JsonException>(() => contract.Read(json.ToJsonString()));
    }

    [Fact]
    public void 格式回显和枚举错误具有独立分类与字段位置()
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0];
        var contract = new ChunkAnalysisContract(input.Source, chunk, Guid.NewGuid(), "test", ChunkAnalysisContract.Passages(input.Source, chunk));
        var echo = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(contract.JsonSchema + Good().Text, contract));
        Assert.Equal(ModelDiagnosticCode.SchemaEcho, echo.Diagnostic!.Code);
        // 实际响应把服务端 response_format 的 json_object 混进了 Schema；分类仍需指出回显，而非笼统报 JSON 错误。
        var mixed = JsonNode.Parse(contract.JsonSchema)!; mixed["type"] = "json_object";
        var mixedEcho = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(mixed.ToJsonString() + Good().Text, contract));
        Assert.Equal(ModelDiagnosticCode.SchemaEcho, mixedEcho.Diagnostic!.Code);
        var bad = JsonNode.Parse(Good().Text)!; bad["Findings"]![0]!["Kind"] = "CharacterClaim";
        var error = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(bad.ToJsonString(), contract));
        Assert.Equal(ModelDiagnosticCode.ContractMismatch, error.Diagnostic!.Code);
        Assert.Equal("$.Findings[0].Kind", error.Diagnostic.Path);
        Assert.DoesNotContain("CharacterClaim", error.Message);
    }

    [Fact]
    public async Task 收到用量后断流仍保存费用且不能自动纠正()
    {
        const string stream = """data: {"choices":[{"index":0,"delta":{"content":"半份"},"finish_reason":null}],"usage":{"prompt_tokens":23,"completion_tokens":7}}""";
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => DeepSeekTextModel.ReadEventsAsync(new StringReader(stream + "\n\n"), null, default));
        Assert.Equal(new ModelUsage(23, 7), error.ObservedUsage);
        Assert.Equal(ModelDiagnosticCode.StreamIncomplete, error.Diagnostic!.Code);
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => throw error); var fixture = new Fixture(workspace, model); var run = await fixture.Create();
        Assert.Equal(AnalysisRunState.NeedsAttention, (await fixture.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        var entry = Assert.Single(fixture.Requests.List(run.Budget.Id));
        Assert.Equal(30, entry.ChargedTokens); Assert.False(entry.ResponseComplete); Assert.False(entry.RetryAcknowledged);
    }

    [Fact]
    public async Task 第三单元格式失败只纠正一次且前两个不重复发送()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Good(), _ => Good(), r => Good() with { Text = r.Contract!.JsonSchema }, r =>
        { Assert.Contains("本次只纠正", r.SystemPrompt); return Good(); });
        var fixture = new Fixture(workspace, model); var run = await fixture.Create(3);
        var finished = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, finished.State); Assert.Equal(4, model.Requests.Count);
        Assert.Equal(1, finished.Nodes[2].FormatRetries); Assert.Equal(3, fixture.Service.ReadExtractions(run.Id).Count);
        Assert.Equal(1200, fixture.Requests.List(run.Budget.Id).Sum(e => e.ChargedTokens));
        await fixture.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(4, model.Requests.Count);
    }

    [Fact]
    public async Task 连续格式失败和进程恢复不能重置自动重试次数()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(r => Good() with { Text = r.Contract!.JsonSchema }, r => Good() with { Text = r.Contract!.JsonSchema });
        var fixture = new Fixture(workspace, model); var run = await fixture.Create();
        Assert.Equal(AnalysisRunState.NeedsAttention, (await fixture.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(2, model.Requests.Count); Assert.Equal(1, fixture.Runs.Read(run.Id).Nodes[0].FormatRetries);
    }

    [Theory]
    [InlineData(true, 100)]
    [InlineData(false, 2)]
    public async Task 未知费用或后续额度不足不触发格式重试(bool unknown, int requests)
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(r => Good() with { Text = r.Contract!.JsonSchema, Usage = unknown ? new(null, null) : new(100, 200) });
        var fixture = new Fixture(workspace, model); var run = await fixture.Create(requests: requests);
        Assert.Equal(AnalysisRunState.NeedsAttention, (await fixture.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Single(model.Requests); Assert.False(Assert.Single(fixture.Requests.List(run.Budget.Id)).RetryAcknowledged);
    }

    [Fact]
    public async Task 修订仅为未完成节点使用新参数并保留前三个成功结果()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Good(), _ => Good(), _ => Good(), _ => throw new IOException("断流"),
            request => { Assert.Equal(16384, request.EffectivePreset.MaxOutputTokens); Assert.Equal("none", request.EffectivePreset.ReasoningEffort); return Good(); });
        var fixture = new Fixture(workspace, model); var original = await fixture.Create(4);
        await fixture.Service.ExecuteAsync(original.Id, new(), null, default);
        var saved = fixture.Runs.Read(original.Id); var stamps = saved.Nodes.Take(3).Select(n => n.InputStamp).ToArray();
        fixture.Service.Resume(original.Id, true, 100, 8000000);
        var old = original.Connection.Connection;
        var changed = await workspace.Connections.SaveAsync(old, old.Settings with { Checking = new("deepseek-flash", 32768, "high") });
        var frozen = new FrozenConnection(original.BookId, changed, ModelTask.Checking, changed.Settings.Checking);
        var revision = await fixture.Service.CreateAsync(original.BookId, ConnectionService.Bind(changed), 100, 8000000, original.ReportReserve,
            default, previousRunId: original.Id, stageSettings: AnalysisStageSettings.Default(frozen));
        var result = await fixture.Service.ExecuteAsync(revision.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, result.State); Assert.Equal(5, model.Requests.Count);
        Assert.Equal(stamps, result.Nodes.Take(3).Select(n => n.InputStamp));
        Assert.All(result.Nodes.Take(3), n => Assert.Equal(old, n.ExecutionConnection!.Connection));
        Assert.Equal(changed, result.Nodes[3].ExecutionConnection!.Connection);
        Assert.Equal(original.Budget.Id, result.Budget.Id); Assert.Equal(5, fixture.Service.Usage(result.Id).Count);
    }

    [Fact]
    public async Task 旧运行缺少阶段设置时请求和输入指纹不变()
    {
        await using var workspace = new TestWorkspace();
        var fixture = new Fixture(workspace, new ScriptedTextModel(_ => Good())); var run = await fixture.Create();
        var node = run.Nodes[0] with { ExecutionConnection = null, ExecutionPreset = null, ExtractionPromptVersion = "v3" };
        var old = run with { Nodes = [node], StageSettings = null }; var input = fixture.Sources.Read(run.BookId);
        var actual = new NovelAnalysisNodePreparer().Prepare(old, input, node, new Dictionary<string, AnalysisNodeResult>());
        var expected = NovelChunkAnalysisService.Prepare(input, input.Chunks[0], run.Connection, node.OperationId, "v3");
        Assert.Equal(expected.InputStamp, actual.InputStamp); Assert.Equal(run.Connection.Preset, actual.Request.EffectivePreset);
    }

    [Fact]
    public async Task 阶段参数不伪造授权连接且失效连接不能发送()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Good()); var fixture = new Fixture(workspace, model); var seed = await fixture.Create();
        var settings = AnalysisStageSettings.Default(seed.Connection);
        var run = await fixture.Service.CreateAsync(seed.BookId, ConnectionService.Bind(seed.Connection.Connection), 100, 8000000,
            seed.ReportReserve, default, stageSettings: settings);
        var prepared = new NovelAnalysisNodePreparer().Prepare(run, fixture.Sources.Read(run.BookId), run.Nodes[0], new Dictionary<string, AnalysisNodeResult>());
        Assert.Equal(seed.Connection, prepared.Request.Configuration); Assert.Equal(settings.Extraction, prepared.Request.EffectivePreset); prepared.Request.Validate();
        await workspace.Connections.SaveAsync(seed.Connection.Connection, seed.Connection.Connection.Settings with { Endpoint = "https://changed.example" });
        Assert.Equal(AnalysisRunState.NeedsAttention, (await fixture.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 旧运行库升级前创建一致性备份且旧记录仍可读(int version)
    {
        await using var workspace = new TestWorkspace();
        var fixture = new Fixture(workspace, new ScriptedTextModel(_ => Good())); var run = await fixture.Create();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.Runs.DatabasePath + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA user_version={version}"; command.ExecuteNonQuery();
        }
        Assert.Equal(run.Id, fixture.Runs.Read(run.Id).Id);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(workspace.Paths.Root, "Backups"), $"reference-runs-schema{version}-*.db"));
        using var copied = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + backup + ";Mode=ReadOnly;Pooling=False"); copied.Open();
        using var read = copied.CreateCommand(); read.CommandText = "PRAGMA user_version"; Assert.Equal((long)version, read.ExecuteScalar());
        read.CommandText = "SELECT count(*) FROM runs"; Assert.Equal(1L, read.ExecuteScalar());
        fixture.Runs.Read(run.Id); Assert.Single(Directory.GetFiles(Path.Combine(workspace.Paths.Root, "Backups")));
    }

    private sealed class CompleteBodyBrokenStream : ITextModel
    {
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(Good().Text);
            throw new ModelRequestException(ModelFailure.Protocol, "断流")
            { ObservedUsage = new(100, 200), Diagnostic = new(ModelDiagnosticCode.StreamIncomplete) };
        }
    }
    [Fact]
    public async Task 断流即使已有合法JSON和用量也不能误采纳为完整响应()
    {
        await using var workspace = new TestWorkspace();
        var fixture = new Fixture(workspace, new CompleteBodyBrokenStream()); var run = await fixture.Create();
        await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.AdoptReviewedCandidate(run.Id));
        Assert.Empty(fixture.Service.ReadExtractions(run.Id));
    }

    [Fact]
    public async Task 长SSE信封不会被固定八百万字符限制误判而低额度仍有限制()
    {
        var envelope = ": " + new string('x', 8000) + "\n\n";
        var body = string.Concat(Enumerable.Repeat(envelope, 1001)) +
            """data: {"choices":[{"index":0,"delta":{"content":"完成"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""" + "\n\ndata: [DONE]\n\n";
        Assert.Equal("完成", (await DeepSeekTextModel.ReadEventsAsync(new StringReader(body), null, default, 16384)).Text);
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => DeepSeekTextModel.ReadEventsAsync(new StringReader(body), null, default, 256));
        Assert.Equal(ModelDiagnosticCode.StreamLimit, error.Diagnostic!.Code);
    }
}
