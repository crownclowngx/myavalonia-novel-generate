using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Import;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class NovelReportTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
    private static IndexedFinding Fact(int number) => new(number, 1, new(Guid.NewGuid(), AnalysisDimension.Style, "语言", "短句推动节奏。", AnalysisStatementKind.Inferred, []));
    private static NovelReportDraft Draft(params int[] ids) => new("报告", [new("节奏", "短句加快动作推进。", AnalysisStatementKind.Inferred, [.. ids])], []);

    [Fact]
    public void 报告引用越界空结论无说明及过长节点均拒绝()
    {
        var contract = new NovelReportContract([Fact(1)]);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(Draft(99))));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(new NovelReportDraft("报告", [], []))));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(Draft(1) with { Claims = [Draft(1).Claims[0] with { Text = new('甲', 1201) }] })));
        Assert.Throws<InvalidDataException>(() => contract.Read(new string(' ', 32001) + Json(Draft(1))));
        Assert.Throws<JsonException>(() => contract.Read(Json(Draft(1)).Replace("Inferred", "Certain", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(Draft(1) with { Claims = [Draft(1).Claims[0] with { Certainty = AnalysisStatementKind.Explicit }] })));
    }

    [Fact]
    public void 文风必须引用当轮回看的真实语言样本()
    {
        var contract = new NovelReportContract([Fact(1), Fact(2)], new HashSet<int> { 1 });
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(Draft(2))));
        Assert.Single(contract.Read(Json(Draft(1, 2))).Claims);
    }

    [Fact]
    public void 代表性选择稳定覆盖首中尾且不重复()
    {
        var values = Enumerable.Range(1, 101).ToArray(); var selected = NovelReportPlanner.Representative(values, 5);
        Assert.Equal(new[] { 1, 26, 51, 76, 101 }, selected.ToArray());
        Assert.Equal(values, NovelReportPlanner.Representative(values, 200).ToArray());
    }

    [Fact]
    public async Task 自动产出六专题与综合报告并保持来源与离线可读()
    {
        await using var workspace = new TestWorkspace(); var model = new Router(); var context = await Setup(workspace, model);
        Assert.Equal(AnalysisRunState.Completed, (await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default)).State);
        var report = context.Reports.Read(context.Run.Id); Assert.True(report.IsComplete); Assert.Equal(6, report.Parts.Length); Assert.NotNull(report.Synthesis);
        Assert.Equal(report.SourceCharacters, report.CoveredCharacters); Assert.Contains("待人工", report.QualityStatus);
        File.Delete(context.InputPath); var offline = context.Reports.Read(context.Run.Id); Assert.Equal(report.Version, offline.Version);
        var evidence = offline.Integrated.Index.Findings[0].Value.Evidence[0]; var location = context.Reports.Locate(context.Run.Id, evidence);
        Assert.Equal(evidence.Quote, location.Excerpt.Substring(location.SelectionStart, location.Length)); Assert.Equal(report.TextHash, location.SourceHash);
        var markdown = NovelReportMarkdown.Format(offline);
        Assert.Contains("## 综合结论", markdown); Assert.Contains(report.ByteHash, markdown); Assert.Contains("原著完整性未经确认", markdown);
        foreach (var dimension in Enum.GetValues<AnalysisDimension>()) Assert.Contains("## " + NovelReportRequests.Title(dimension), markdown);
        foreach (var claim in report.Parts.SelectMany(p => p.Draft.Claims).Concat(report.Synthesis!.Claims))
            foreach (var id in claim.Facts) Assert.Contains($"<a id=\"fact-{id}\"></a>", markdown);
        var count = model.Requests.Count; await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default); Assert.Equal(count, model.Requests.Count);
    }

    [Fact]
    public async Task 未齐全专题明确显示部分报告并能接续完成()
    {
        await using var workspace = new TestWorkspace(); var control = new AnalysisRunControl(); var model = new Router { PauseOnDimension = control }; var context = await Setup(workspace, model);
        Assert.Equal(AnalysisRunState.Paused, (await context.Runner.ExecuteAsync(context.Run.Id, control, null, default)).State);
        var partial = context.Reports.Read(context.Run.Id); Assert.False(partial.IsComplete); Assert.Single(partial.Parts); Assert.Null(partial.Synthesis);
        Assert.Contains("部分报告", NovelReportMarkdown.Format(partial));
        Assert.Equal(AnalysisRunState.Completed, (await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default)).State);
        Assert.True(context.Reports.Read(context.Run.Id).IsComplete);
    }

    [Fact]
    public async Task 多阶段汇总形成有界树并对摘要节点故障恢复()
    {
        await using var workspace = new TestWorkspace(); var model = new Router { FailSummaryOnce = true }; var context = await Setup(workspace, model, 20);
        var interrupted = await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default); Assert.Equal(AnalysisRunState.NeedsAttention, interrupted.State);
        var extractions = model.Requests.Count(r => r.Contract is ChunkAnalysisContract); Assert.Equal(20, extractions);
        context.Runner.Resume(context.Run.Id, true, 80, 3000000);
        var completed = await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default); Assert.Equal(AnalysisRunState.Completed, completed.State);
        Assert.Contains(completed.Nodes, n => n.Kind == AnalysisNodeKind.Summary && n.Layer > 0);
        Assert.All(completed.Nodes.Where(n => n.Kind == AnalysisNodeKind.Summary && n.Layer > 0), n => Assert.InRange(n.Dependencies.Length, 1, 3));
        Assert.Equal(20, model.Requests.Count(r => r.Contract is ChunkAnalysisContract)); Assert.True(context.Reports.Read(context.Run.Id).IsComplete);
        Assert.All(model.Requests, r => Assert.True(r.UserPrompt.Length <= 70000));
    }

    [Fact]
    public async Task 已完成报告的依赖被损坏时拒绝冒充完整()
    {
        await using var workspace = new TestWorkspace(); var context = await Setup(workspace, new Router()); await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        using (var connection = new SqliteConnection("Data Source=" + context.Store.DatabasePath + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE results SET json='{}' WHERE key='dimension-World'"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => context.Reports.Read(context.Run.Id));
    }

    [Fact]
    public async Task 缺少提取时不能伪造报告且引用不能跨来源定位()
    {
        await using var workspace = new TestWorkspace(); var context = await Setup(workspace, new Router());
        Assert.Throws<InvalidOperationException>(() => context.Reports.Read(context.Run.Id));
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default); var report = context.Reports.Read(context.Run.Id);
        var evidence = report.Integrated.Index.Findings[0].Value.Evidence[0] with { SourceId = Guid.NewGuid() };
        Assert.Throws<InvalidDataException>(() => context.Reports.Locate(context.Run.Id, evidence));
    }

    [Fact]
    public async Task Markdown转义模型标记并保持可核查来源链接()
    {
        await using var workspace = new TestWorkspace(); var context = await Setup(workspace, new Router()); await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        var report = context.Reports.Read(context.Run.Id); var synthesis = report.Synthesis!;
        report = report with { Synthesis = synthesis with { Claims = [synthesis.Claims[0] with { Text = "<script>bad</script> [执行](https://example.com)" }] } };
        var markdown = NovelReportMarkdown.Format(report); Assert.DoesNotContain("<script>", markdown); Assert.Contains("&lt;script&gt;", markdown); Assert.Contains("\\[执行\\]", markdown);
    }

    internal sealed record Context(NovelAnalysisRunService Runner, NovelAnalysisReportService Reports, AnalysisRun Run, AnalysisRunStore Store, string InputPath);

    [Fact]
    public async Task 五阶段参数实际生效且完整报告修订全部复用原配置成果()
    {
        await using var workspace = new TestWorkspace(); var model = new Router(); var context = await Setup(workspace, model);
        var settings = AnalysisStageSettings.Default(context.Run.Connection);
        var run = await context.Runner.CreateAsync(context.Run.BookId, ConnectionService.Bind(context.Run.Connection.Connection), context.Run.Budget.MaximumRequests,
            context.Run.Budget.MaximumTokens, context.Run.ReportReserve, default, previousRunId: context.Run.Id, target: AnalysisTarget.Report, stageSettings: settings);
        var complete = await context.Runner.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.Completed, complete.State);
        foreach (var request in model.Requests)
        {
            var node = complete.Nodes.Single(n => n.OperationId == request.OperationId);
            Assert.Equal(settings.For(node.Kind), request.EffectivePreset);
        }
        var report = context.Reports.Read(run.Id); Assert.Contains("实际执行配置", NovelReportMarkdown.Format(report));
        Assert.Equal(5, report.ExecutionSummary.Length);
        var before = model.Requests.Count;
        var next = await context.Runner.CreateAsync(run.BookId, ConnectionService.Bind(run.Connection.Connection), run.Budget.MaximumRequests, run.Budget.MaximumTokens,
            run.ReportReserve, default, previousRunId: run.Id, target: AnalysisTarget.Report,
            stageSettings: settings with { Summary = settings.Summary with { MaxOutputTokens = 32768 } });
        Assert.Equal(AnalysisRunState.Completed, (await context.Runner.ExecuteAsync(next.Id, new(), null, default)).State);
        Assert.Equal(before, model.Requests.Count);
        Assert.Equal(report.ExecutionSummary.ToArray(), context.Reports.Read(next.Id).ExecutionSummary.ToArray());
    }
    internal static async Task<Context> Setup(TestWorkspace workspace, ITextModel model, int chapters = 3)
    {
        var input = Path.Combine(workspace.Root, "报告样本.txt"); await File.WriteAllTextAsync(input, string.Concat(Enumerable.Range(1, chapters).Select(i => $"第{i}章\n林远来到雾港，走进城门。\n")));
        var sourceStore = new ReferenceSourceStore(workspace.Paths); var store = new AnalysisRunStore(workspace.Paths);
        var importer = new NovelImportService(new TxtSourceReader(), sourceStore); var preview = await importer.PreviewAsync(input, null, new(6000, 0), default); var book = await importer.ImportAsync(preview, default);
        var preset = new ModelPreset("deepseek-flash", 16384, "none"); var connection = await workspace.Connections.SaveAsync(null, new("报告测试", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
        var runner = new NovelAnalysisRunService(sourceStore, store, workspace.Connections, new(model, new ModelRequestStore(workspace.Paths)), new NovelAnalysisNodePreparer());
        var run = await runner.CreateAsync(book.Id, ConnectionService.Bind(connection), 80, 3000000, new(20, 600000), default, target: AnalysisTarget.Report);
        return new(runner, new(sourceStore, store), run, store, input);
    }

    internal sealed class Router : ITextModel
    {
        public List<TextModelRequest> Requests { get; } = [];
        public AnalysisRunControl? PauseOnDimension { get; set; }
        public bool FailSummaryOnce { get; set; }
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); string json;
            using var prompt = JsonDocument.Parse(request.UserPrompt);
            if (request.Contract is NovelBatchAnalysisContract)
                return Task.FromResult(NovelBatchAnalysisTests.Good(request));
            if (request.Contract is ChunkAnalysisContract)
            {
                var basic = NovelChunkAnalysisTests.Output();
                json = Json(basic with { Findings = [.. Enum.GetValues<AnalysisDimension>().Select(d => basic.Findings[0] with { Dimension = d })], Gaps = [] });
            }
            else if (request.Contract is IdentityAnalysisContract or ContinuityAnalysisContract)
            {
                var ids = prompt.RootElement.GetProperty("Items").EnumerateArray().Select(i => i.GetProperty("Number").GetInt32()).ToImmutableArray();
                json = request.Contract is IdentityAnalysisContract
                    ? Json(new IdentityOutput([new("林远", StoryEntityKind.Person, ids, AnalysisStatementKind.Inferred, "称呼与角色一致。")], []))
                    : Json(new ContinuityOutput([new("林远", "林远来到雾港。", AnalysisStatementKind.Inferred, NarrativeSource.Narration, "正文当前时间", ContinuityRole.Event, "", "", [.. ids.Take(16)])], []));
            }
            else
            {
                if (FailSummaryOnce) { FailSummaryOnce = false; throw new IOException("摘要断流"); }
                var material = prompt.RootElement.GetProperty("Material"); var ids = new List<int>();
                if (material.TryGetProperty("Samples", out var samples))
                {
                    ids.AddRange(samples.EnumerateArray().Select(s => s.GetProperty("Number").GetInt32()));
                    PauseOnDimension?.Pause(); PauseOnDimension = null;
                }
                else if (material.TryGetProperty("Facts", out var facts)) ids.AddRange(facts.EnumerateArray().Select(f => f.GetProperty("Number").GetInt32()));
                else
                {
                    var children = material.TryGetProperty("Children", out var summaries) ? summaries : material.GetProperty("Sections");
                    ids.AddRange(children.EnumerateArray().SelectMany(s => s.GetProperty("Claims").EnumerateArray()).SelectMany(c => c.GetProperty("Facts").EnumerateArray()).Select(id => id.GetInt32()));
                }
                json = ids.Count == 0 ? Json(new NovelReportDraft("当前缺口", [], ["当前材料不足以支持结论。 "])) : Json(Draft([.. ids.Distinct().Take(3)]));
            }
            return Task.FromResult(new TextModelResponse(json, ModelCompletion.Complete, new(100, 100)));
        }
    }
}
