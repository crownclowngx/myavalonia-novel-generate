using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class NovelBatchAnalysisTests
{
    internal static TextModelResponse Good(TextModelRequest request)
    {
        using var prompt = JsonDocument.Parse(request.UserPrompt);
        var parts = prompt.RootElement.GetProperty("Parts").EnumerateArray().Select(p =>
        {
            var passages = p.GetProperty("Passages");
            var passage = passages.GetArrayLength() > 1 ? 2 : 1;
            var basic = NovelChunkAnalysisTests.Output(passage);
            return new
            {
                Part = p.GetProperty("Part").GetInt32(),
                Analysis = basic with
                {
                    Findings = Enum.GetValues<AnalysisDimension>().Select(d => basic.Findings[0] with { Dimension = d }).ToImmutableArray(),
                    Gaps = []
                }
            };
        }).ToArray();
        return new(JsonSerializer.Serialize(new { Parts = parts }, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }),
            ModelCompletion.Complete, new(100, 200));
    }

    private static AnalysisCapacityOptions Auto(long target = 200000, long? context = null) => new(context, 8)
    { AutomaticBatching = true, TargetInputTokens = target };

    private static async Task<(AnalysisRecoveryTests.Fixture Fixture, AnalysisRun Run, ReferenceImport Source)> Setup(TestWorkspace workspace,
        ITextModel model, int chapters = 30, int characters = 1000, AnalysisCapacityOptions? capacity = null, int output = 65536)
    {
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model);
        var text = string.Concat(Enumerable.Range(1, chapters).Select(i => $"第{i}章\n林远走进城门。\n" + new string('字', characters) + "𠮷🙂\n"));
        var seed = ReferenceSourceTests.Create(text);
        var source = NovelTextPartitioner.Partition(seed.Source, seed.Book.Name, new(), default); fixture.Sources.Import(source);
        var preset = new ModelPreset("deepseek-flash", output, "none");
        var connection = await workspace.Connections.SaveAsync(null, new("长上下文测试", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
        var run = await fixture.Service.CreateAsync(source.Book.Id, ConnectionService.Bind(connection), 200, 8000000, new(1, 10000), default, capacity: capacity ?? Auto());
        return (fixture, run, source);
    }

    [Fact]
    public async Task 三十章合为一次请求并逐章保留六维结果和真实证据()
    {
        await using var workspace = new TestWorkspace(); var model = new ScriptedTextModel(Good);
        var (fixture, run, source) = await Setup(workspace, model);
        Assert.Single(run.Chunks); Assert.Equal(30, source.Sections.Length); Assert.Equal(source.Source.Text.Length, run.Chunks[0].Body.Length);
        var finished = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, finished.State); Assert.Single(model.Requests);
        var result = Assert.Single(fixture.Service.ReadExtractions(run.Id)); Assert.Equal(180, result.Findings.Length); Assert.Equal(30, result.Entities.Length);
        Assert.Equal(180, result.Findings.Select(f => f.Id).Distinct().Count());
        Assert.Equal(30, result.Findings.SelectMany(f => f.Evidence).Select(e => source.Sections.Single(s => s.Range.Contains(e.Range)).Id).Distinct().Count());
        Assert.All(result.Findings.SelectMany(f => f.Evidence), e => e.Validate(source.Source));
        Assert.Equal(source.Sections.ToArray(), fixture.Sources.Read(source.Book.Id).Sections.ToArray());
    }

    [Fact]
    public async Task 小窗口和小输出额度会产生更多批次且每批都满足真实请求预检()
    {
        await using var workspace = new TestWorkspace(); var (fixture, large, source) = await Setup(workspace, new ScriptedTextModel(Good), characters: 6000);
        var options = Auto(800000, 100000); var preset = large.Connection.Preset with { MaxOutputTokens = 16384 };
        var small = NovelBatchPlanner.Plan(source, large.Connection, preset, options, default);
        Assert.True(small.Chunks.Length > large.Chunks.Length);
        Assert.Equal(source.Source.Text, string.Concat(small.Chunks.Select(c => source.Source.Text.Substring(c.Body.Start, c.Body.Length))));
        Assert.All(small.Chunks, c =>
        {
            var request = NovelChunkAnalysisService.Prepare(small, c, large.Connection, Guid.NewGuid(), NovelBatchAnalysisContract.PromptVersion).Request
                with
            { ExecutionPreset = preset, ContextTokenLimit = options.ContextTokens };
            request.Validate(); Assert.True(ModelInputCapacity.Estimate(request).TotalReservation <= 100000);
        });
        Assert.Empty(fixture.Service.Usage(large.Id));
    }

    [Fact]
    public async Task 超长单章会在输入和输出预算内完整分批且不拆开代理对()
    {
        await using var workspace = new TestWorkspace(); var (_, run, source) = await Setup(workspace, new ScriptedTextModel(Good), 1, 120000, Auto(32000), 8192);
        Assert.True(run.Chunks.Length > 10);
        var planned = source with { Chunks = run.Chunks }; planned.Validate();
        Assert.Equal(source.Source.Text, string.Concat(run.Chunks.Select(c => source.Source.Text.Substring(c.Body.Start, c.Body.Length))));
    }

    [Fact]
    public async Task 批次缺章重复编号或越界证据均不能被视为全文完成()
    {
        await using var workspace = new TestWorkspace(); var (_, run, source) = await Setup(workspace, new ScriptedTextModel(Good), 3);
        var input = source with { Chunks = run.Chunks };
        var prepared = new NovelAnalysisNodePreparer().Prepare(run, input, run.Nodes[0], new Dictionary<string, AnalysisNodeResult>());
        var json = JsonNode.Parse(Good(prepared.Request).Text)!;
        var parts = json["Parts"]!.AsArray(); parts.RemoveAt(2);
        Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(json.ToJsonString(), prepared.Request.Contract));
        json = JsonNode.Parse(Good(prepared.Request).Text)!; json["Parts"]![1]!["Part"] = 1;
        Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(json.ToJsonString(), prepared.Request.Contract));
        json = JsonNode.Parse(Good(prepared.Request).Text)!; json["Parts"]![1]!["Analysis"]!["Findings"]![0]!["Evidence"]![0]!["Passage"] = 999;
        Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(json.ToJsonString(), prepared.Request.Contract));
    }

    [Fact]
    public async Task 已知输出截断跨章二分后恢复且修订不重新发送已完成批次()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(r => Good(r) with { Completion = ModelCompletion.Truncated }, Good, Good);
        var (fixture, run, source) = await Setup(workspace, model, 20);
        var finished = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, finished.State); Assert.Single(finished.Splits); Assert.Equal(3, model.Requests.Count);
        var reopened = new AnalysisRecoveryTests.Fixture(workspace, model);
        var saved = reopened.Runs.Read(run.Id); (source with { Chunks = saved.Chunks }).Validate();
        Assert.Equal(source.Source.Text.Length, saved.Chunks.Sum(c => c.Body.Length));
        Assert.Equal(900, reopened.Service.Usage(run.Id).Sum(e => e.ChargedTokens));
        var revised = await reopened.Service.CreateAsync(run.BookId, ConnectionService.Bind(run.Connection.Connection), 200, 8000000,
            run.ReportReserve, default, previousRunId: run.Id, capacity: Auto());
        Assert.Equal(saved.Chunks.ToArray(), revised.Chunks.ToArray());
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await reopened.Service.ExecuteAsync(revised.Id, new(), null, default)).State);
        Assert.Equal(3, model.Requests.Count); Assert.Equal(20, reopened.Service.ReadExtractions(revised.Id).Sum(r => r.Entities.Length));
    }

    [Fact]
    public async Task 未知用量的截断不会自动拆批重发()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(r => Good(r) with { Completion = ModelCompletion.Truncated, Usage = new(null, null) });
        var (fixture, run, _) = await Setup(workspace, model, 20);
        var finished = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.NeedsAttention, finished.State); Assert.Empty(finished.Splits); Assert.Single(model.Requests);
    }

    [Fact]
    public async Task 批次响应落账后崩溃可从账本恢复而不再调用模型()
    {
        await using var workspace = new TestWorkspace(); var model = new ScriptedTextModel(Good);
        var (fixture, run, source) = await Setup(workspace, model);
        var prepared = new NovelAnalysisNodePreparer().Prepare(run, source with { Chunks = run.Chunks }, run.Nodes[0], new Dictionary<string, AnalysisNodeResult>());
        var running = run with { Version = 2, State = AnalysisRunState.Running, Nodes = [run.Nodes[0] with { State = AnalysisNodeState.Running, InputStamp = prepared.InputStamp }] };
        fixture.Runs.Save(running, 1);
        await fixture.Requests.GenerateAsync(prepared.Request, run.Budget, null, default);
        var reopened = new AnalysisRecoveryTests.Fixture(workspace, model);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await reopened.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Single(model.Requests); Assert.Equal(30, Assert.Single(reopened.Service.ReadExtractions(run.Id)).Entities.Length);
    }

    [Fact]
    public async Task 暂停后重开继续并在修改容量修订时保留已完成批次()
    {
        await using var workspace = new TestWorkspace(); var control = new AnalysisRunControl();
        var model = new ScriptedTextModel(new[] { (Func<TextModelRequest, TextModelResponse>)(r => { control.Pause(); return Good(r); }) }
            .Concat(Enumerable.Repeat<Func<TextModelRequest, TextModelResponse>>(Good, 100)).ToArray());
        var (fixture, run, _) = await Setup(workspace, model, 30, 3000, Auto(60000));
        var paused = await fixture.Service.ExecuteAsync(run.Id, control, null, default); Assert.Equal(AnalysisRunState.Paused, paused.State);
        var completed = Assert.Single(paused.Nodes.Where(n => n.State == AnalysisNodeState.Completed));
        var revised = await fixture.Service.CreateAsync(run.BookId, ConnectionService.Bind(run.Connection.Connection), 200, 8000000,
            run.ReportReserve, default, previousRunId: run.Id, capacity: Auto(100000));
        Assert.Contains(revised.Chunks, c => c.Id == completed.ChunkId);
        var finished = await new AnalysisRecoveryTests.Fixture(workspace, model).Service.ExecuteAsync(revised.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, finished.State);
        Assert.Equal(completed.InputStamp, finished.Nodes.Single(n => n.ChunkId == completed.ChunkId).InputStamp);
        Assert.Single(model.Requests, r => r.OperationId == completed.OperationId);
        Assert.Equal(finished.Chunks.Length, model.Requests.Count);
    }

    [Fact]
    public async Task 已知模型允许超过二十五万字符且未知模型需配置容量()
    {
        await using var workspace = new TestWorkspace(); var (_, run, source) = await Setup(workspace, new ScriptedTextModel(Good));
        var request = new TextModelRequest(Guid.NewGuid(), run.Connection, "分析文本", new string('a', 300000), false);
        request.Validate(); Assert.False(ModelInputCapacity.Estimate(request).Exceeded);
        var unknown = run.Connection.Preset with { Model = "custom-model" };
        Assert.Throws<InvalidDataException>(() => NovelBatchPlanner.Plan(source, run.Connection, unknown, Auto(), default));
        Assert.False(NovelBatchPlanner.Plan(source, run.Connection, unknown, Auto(context: 1000000), default).Chunks.IsEmpty);
        Assert.Equal(1000000, ModelInputCapacity.Estimate(request with { ContextTokenLimit = 2000000 }).ContextTokens);
        Assert.Throws<ModelRequestException>(() => (request with { ContextTokenLimit = 100000 }).Validate());
    }

    [Fact]
    public async Task 批次预览不创建运行不发送请求且取消规划不留下半份计划()
    {
        await using var workspace = new TestWorkspace(); var model = new ScriptedTextModel(Good);
        var (fixture, run, source) = await Setup(workspace, model);
        var text = await fixture.Service.PreviewBatchesAsync(run.BookId, ConnectionService.Bind(run.Connection.Connection), null, Auto(), default);
        Assert.Contains("首轮 1 个分析批次", text); Assert.Empty(model.Requests); Assert.Single(fixture.Runs.List(run.BookId));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NovelBatchPlanner.Plan(source, run.Connection, run.Connection.Preset, Auto(), cancellation.Token));
        Assert.Single(fixture.Runs.List(run.BookId));
    }

    [Fact]
    public async Task 自动分批能完成六专题报告并离线定位所有章节证据()
    {
        await using var workspace = new TestWorkspace(); var router = new NovelReportTests.Router(); var context = await NovelReportTests.Setup(workspace, router, 10);
        var run = await context.Runner.CreateAsync(context.Run.BookId, ConnectionService.Bind(context.Run.Connection.Connection), 200, 8000000,
            context.Run.ReportReserve, default, target: AnalysisTarget.Report, capacity: Auto());
        Assert.Equal(AnalysisRunState.Completed, (await context.Runner.ExecuteAsync(run.Id, new(), null, default)).State);
        File.Delete(context.InputPath);
        var report = context.Reports.Read(run.Id); Assert.True(report.IsComplete); Assert.Equal(6, report.Parts.Length);
        Assert.Equal(report.SourceCharacters, report.CoveredCharacters);
        var locations = report.Integrated.Index.Findings.SelectMany(f => f.Value.Evidence).Select(e => context.Reports.Locate(run.Id, e));
        Assert.Equal(10, locations.Select(l => l.Number).Distinct().Count());
    }

    [Fact]
    public async Task 两千个短章节按批次数检查请求预算而不是被章节数拒绝()
    {
        await using var workspace = new TestWorkspace(); var model = new ScriptedTextModel(Good);
        var (fixture, run, source) = await Setup(workspace, model, 2000, 200);
        Assert.Equal(2000, source.Sections.Length); Assert.InRange(run.Chunks.Length, 2, 199);
        Assert.Equal(source.Source.Text.Length, run.Chunks.Sum(c => c.Body.Length)); Assert.Empty(fixture.Service.Usage(run.Id));
    }

    [Fact]
    public async Task 自动批次不会越过未选择的章节且错误起始章被拒绝()
    {
        await using var workspace = new TestWorkspace(); var (_, run, source) = await Setup(workspace, new ScriptedTextModel(Good), 3);
        var excluded = source with
        {
            Sections = source.Sections.SetItem(1, source.Sections[1] with { Included = false }),
            Chunks = source.Chunks.Where(c => c.SectionId != source.Sections[1].Id).Select((c, i) => c with { Number = i + 1 }).ToImmutableArray()
        };
        var plan = NovelBatchPlanner.Plan(excluded, run.Connection, run.Connection.Preset, Auto(), default);
        Assert.Equal(2, plan.Chunks.Length); plan.Validate();
        Assert.Throws<InvalidDataException>(() => (excluded with { Chunks = run.Chunks }).Validate());
        Assert.Throws<InvalidDataException>(() => (source with { Chunks = [run.Chunks[0] with { SectionId = source.Sections[1].Id }] }).Validate());
    }
}
