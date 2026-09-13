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
        Assert.Throws<InvalidDataException>(() => contract.Read(json.ToJsonString()));
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
}
