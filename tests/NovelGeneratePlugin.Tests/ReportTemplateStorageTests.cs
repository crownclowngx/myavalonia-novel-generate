using System.Collections.Immutable;
using System.Text.Json;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class ReportTemplateStorageTests
{
    internal static async Task<ReportTemplateConversion> Conversion(TestWorkspace workspace)
    {
        var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
        var source = new ReportTemplateSource(context.Run.BookId, context.Run.Id, context.Run.SourceId, context.Run.SourceHash,
            new string('a', 64), "雾港", [new(1, ProfileDimensions.All, "文风", "通过动作推动节奏", TemplateRuleBasis.Inferred, [new(1, "林远推开门。")])], []);
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), source, "雾港创作模板", ProfileDimensions.Style,
            context.Run.Connection, context.Run.Connection.Preset, 131072, new(Guid.NewGuid(), 8, 1000000),
            [new("extract-1", TemplateConversionStepKind.Extract, ProfileDimensions.Style, [1], [], Guid.NewGuid(), TemplateConversionStepState.Pending, "", null, 0, "")],
            TemplateConversionState.Queued, null, "", now, now);
    }
    internal static TemplateConversionCandidate Candidate(ProfileDimensions dimension = ProfileDimensions.Style) =>
        new([new(dimension, [new("优先用动作和对白表现紧张。", "冲突场景", TemplateRuleBasis.Inferred, [1])], [])]);

    [Fact]
    public async Task 分析跨实例重读不依赖原文件且缺失结果不可伪装完成()
    {
        await using var workspace = new TestWorkspace(); var model = new NovelReportTests.Router();
        var context = await NovelReportTests.Setup(workspace, model);
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        var report = context.Reports.Read(context.Run.Id); var requests = model.Requests.Count; File.Delete(context.InputPath);
        var reader = new NovelAnalysisReportService(new ReferenceSourceStore(workspace.Paths), new AnalysisRunStore(workspace.Paths));
        Assert.Equal(report.Version, reader.Read(context.Run.Id).Version); Assert.Equal(requests, model.Requests.Count);
        using var connection = ProjectStore.Connect(context.Store.DatabasePath); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM results WHERE run=$run AND key=$key";
        command.Parameters.AddWithValue("$run", context.Run.Id.ToString());
        command.Parameters.AddWithValue("$key", context.Store.Read(context.Run.Id).Nodes[^1].Key); command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => reader.Read(context.Run.Id)); Assert.Equal(requests, model.Requests.Count);
    }

    [Fact]
    public async Task 历史运行超过一百条仍可分页读回且书目隔离()
    {
        await using var workspace = new TestWorkspace();
        var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
        for (var i = 0; i < 103; i++) context.Store.Create(context.Run with { Id = Guid.NewGuid() });
        var store = new AnalysisRunStore(workspace.Paths);
        var first = store.List(context.Run.BookId); var rest = store.List(context.Run.BookId, first.Count);
        Assert.Equal(100, first.Count); Assert.Equal(4, rest.Count);
        Assert.Contains(rest, r => r.Id == context.Run.Id); Assert.Equal(104, first.Concat(rest).Select(r => r.Id).Distinct().Count());
        Assert.Empty(store.List(Guid.NewGuid())); Assert.Throws<ArgumentOutOfRangeException>(() => store.List(context.Run.BookId, -1));
    }

    [Theory]
    [InlineData("count", "$.Sections[0].Rules")]
    [InlineData("length", "$.Sections[0].Rules[0].Text")]
    [InlineData("reference", "$.Sections[0].Rules[0].Sources[0]")]
    [InlineData("certainty", "$.Sections[0].Rules[0].Basis")]
    [InlineData("dimension", "$.Sections[0].Dimension")]
    public async Task 契约边界能定位字段且不提升来源置信度(string failure, string path)
    {
        await using var workspace = new TestWorkspace(); var run = await Conversion(workspace);
        var contract = new ReportTemplateContract(run.Dimensions, run.Source.Claims);
        var candidate = Candidate(); var section = candidate.Sections[0]; var rule = section.Rules[0];
        candidate = candidate with
        {
            Sections = [failure switch
        {
            "count" => section with { Rules = [.. Enumerable.Repeat(rule, ConversionLimits.RulesPerSection + 1)] },
            "length" => section with { Rules = [rule with { Text = new string('甲', ConversionLimits.RuleCharacters + 1) }] },
            "reference" => section with { Rules = [rule with { Sources = [999] }] },
            "certainty" => section with { Rules = [rule with { Basis = TemplateRuleBasis.Supported }] },
            _ => section with { Dimension = ProfileDimensions.World }
        }]
        };
        var error = Assert.Throws<ModelContractException>(() => contract.Read(ReportTemplateContract.Serialize(candidate))); Assert.Equal(path, error.Path);
        using var schema = JsonDocument.Parse(contract.JsonSchema);
        Assert.Equal(ConversionLimits.RulesPerSection, schema.RootElement.GetProperty("properties").GetProperty("Sections").GetProperty("items").GetProperty("properties").GetProperty("Rules").GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task 转换冻结身份和完成结果不能被陈旧窗口覆盖且恢复副本可重读()
    {
        await using var workspace = new TestWorkspace(); var run = await Conversion(workspace);
        var store = new ReportTemplateConversionStore(workspace.Paths); store.Create(run);
        using (store.Acquire(run.Id)) Assert.Throws<IOException>(() => new ReportTemplateConversionStore(workspace.Paths).Acquire(run.Id));
        var changed = run with { Revision = 2, Name = "更换来源任务名称" };
        Assert.Throws<InvalidDataException>(() => store.Save(changed, 1));
        var next = run with { Revision = 2, State = TemplateConversionState.Running, Steps = run.Steps.SetItem(0, run.Steps[0] with { State = TemplateConversionStepState.Running, InputStamp = new string('b', 64) }) };
        store.Save(next, 1); Assert.Throws<InvalidOperationException>(() => store.Save(next, 1));
        var completed = next with
        {
            Revision = 3,
            State = TemplateConversionState.CandidateSaved,
            Candidate = Candidate(),
            Steps = next.Steps.SetItem(0, next.Steps[0] with { State = TemplateConversionStepState.Completed, ResultJson = ReportTemplateContract.Serialize(Candidate()) })
        };
        store.WriteRecovery(completed);
        var reopened = new ReportTemplateConversionStore(workspace.Paths); Assert.Equal(3, reopened.ReadRecovery(run.Id)!.Revision);
        reopened.Save(reopened.ReadRecovery(run.Id)!, 2); reopened.DeleteRecovery(run.Id); Assert.Null(reopened.ReadRecovery(run.Id));
        Assert.Equal(TemplateConversionState.CandidateSaved, reopened.Read(run.Id).State);
        var altered = Candidate() with { Sections = [Candidate().Sections[0] with { Questions = ["试图替换已保存候选"] }] };
        Assert.Throws<InvalidDataException>(() => reopened.Save(completed with { Revision = 4, Candidate = altered }, 3));
        Assert.Single(reopened.List(run.Source.RunId)); Assert.Empty(reopened.List(Guid.NewGuid()));
    }

    [Fact]
    public async Task 转换库损坏或未来格式拒绝读取和盲写()
    {
        await using var workspace = new TestWorkspace(); var run = await Conversion(workspace);
        var store = new ReportTemplateConversionStore(workspace.Paths); store.Create(run);
        using (var connection = ProjectStore.Connect(store.DatabasePath))
        {
            using var command = connection.CreateCommand(); command.CommandText = "UPDATE conversions SET hash='damaged'"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => store.Read(run.Id));
        using (var connection = ProjectStore.Connect(store.DatabasePath))
        { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99;"; command.ExecuteNonQuery(); }
        Assert.Throws<NotSupportedException>(() => store.Create(run with { Id = Guid.NewGuid() }));
        using var verify = ProjectStore.Connect(store.DatabasePath); using var query = verify.CreateCommand();
        query.CommandText = "SELECT count(*) FROM conversions"; Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "PRAGMA user_version"; Assert.Equal(99L, query.ExecuteScalar());
    }

    [Fact]
    public async Task 模板来源经过编辑发布复制及导出导入保留且旧格式不变()
    {
        await using var workspace = new TestWorkspace(); var run = await Conversion(workspace);
        var legacy = new TemplateDraft("旧模板", [], new("", "短句", "", ""), "手工");
        Assert.DoesNotContain("Provenance", JsonSerializer.Serialize(legacy));
        Assert.Null(JsonSerializer.Deserialize<TemplateDraft>(JsonSerializer.Serialize(legacy))!.Provenance);
        var provenance = new TemplateProvenance(run.Id, run.Source.BookId, run.Source.RunId, run.Source.SourceId,
            run.Source.ReportVersion, run.Source.TextHash, run.Source.Stamp, new string('c', 64), DateTimeOffset.UtcNow);
        var asset = await workspace.Templates.CreateAsync(legacy with { Provenance = provenance });
        await using (var tool = new TemplateLibraryTool(workspace.Templates, workspace.Closing))
        {
            await tool.InitializeAsync(); tool.SelectedTemplate = tool.Templates.Single(t => t.Id == asset.Id);
            tool.Style = "编辑后的文风"; await tool.PublishVersionCommand.ExecuteAsync(null);
        }
        asset = await workspace.Templates.ReadAsync(asset.Id);
        Assert.Equal(provenance, asset.Draft.Provenance); Assert.Equal(provenance, Assert.Single(asset.Versions).Provenance);
        var path = Path.Combine(workspace.Root, "模板.json"); await workspace.Artifacts.ExportTemplateAsync(asset.Id, path);
        var imported = await workspace.Artifacts.ImportTemplateAsync(path); Assert.Equal(provenance, imported.Draft.Provenance);
        Assert.Equal(provenance, (await workspace.Templates.CopyAsync(asset)).Draft.Provenance);
        Assert.Equal("编辑后的文风", imported.Draft.Content.Style);
    }
}
