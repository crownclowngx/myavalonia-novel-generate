using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class NovelChunkAnalysisTests
{
    internal static ChunkOutput Output(int passage = 2) => new("林远来到雾港，收到警告。",
        [new("林远", StoryEntityKind.Person, [], "来到雾港的送信人", [new(passage)])],
        [new(AnalysisDimension.Plot, "林远", "林远走进城门", AnalysisStatementKind.Explicit, [], "", NarrativeSource.Narration, [new(passage)])],
        [.. Enum.GetValues<AnalysisDimension>().Where(d => d != AnalysisDimension.Plot).Select(d => new GapOutput(d, "当前片段没有足够依据。"))]);

    internal static string Json(ChunkOutput output) => JsonSerializer.Serialize(output, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

    [Fact]
    public void 六维契约将段内引用转换为精确坐标且恢复身份稳定()
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0]; var operation = Guid.NewGuid();
        var contract = new ChunkAnalysisContract(input.Source, chunk, operation, "stamp", ChunkAnalysisContract.Passages(input.Source, chunk));
        var result = contract.Read(Json(Output()));
        var again = contract.Read(Json(Output()));
        Assert.Equal(result.Findings[0].Id, again.Findings[0].Id);
        Assert.Equal(result.Entities[0].Id, again.Entities[0].Id);
        Assert.Equal(input.Source.Text.IndexOf("林远独自走进城门。", StringComparison.Ordinal), result.Findings[0].Evidence[0].Range.Start);
        Assert.Equal(5, result.Gaps.Length);
        var repeated = Output(4);
        repeated = repeated with { Findings = [repeated.Findings[0] with { Evidence = [new(4)] }] };
        Assert.Equal(input.Source.Text.LastIndexOf("不要回头。", StringComparison.Ordinal), contract.Read(Json(repeated)).Findings[0].Evidence[0].Range.Start);
    }

    [Fact]
    public void 缺字段错误段号伪造引文及维度遗漏均拒绝()
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0];
        var contract = new ChunkAnalysisContract(input.Source, chunk, Guid.NewGuid(), "stamp", ChunkAnalysisContract.Passages(input.Source, chunk));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(Output(999))));
        var output = Output();
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Gaps = [] })));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Findings = [output.Findings[0] with { Evidence = [new(999)] }] })));
        var json = Json(output).Replace("\"Kind\":\"Explicit\",", "", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => contract.Read(json));
        Assert.Throws<JsonException>(() => contract.Read(Json(output).Replace("\"Summary\":", "\"Unexpected\":", StringComparison.Ordinal)));
    }

    [Fact]
    public void 同维度多个信息缺口完整保留但数量仍有上限()
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0];
        var contract = new ChunkAnalysisContract(input.Source, chunk, Guid.NewGuid(), "stamp", ChunkAnalysisContract.Passages(input.Source, chunk));
        var output = Output() with { Gaps = [.. Output().Gaps, new(AnalysisDimension.World, "力量代价未知。"), new(AnalysisDimension.World, "地理范围未知。")] };
        Assert.Equal(7, contract.Read(Json(output)).Gaps.Length);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Gaps = Enumerable.Repeat(new GapOutput(AnalysisDimension.World, "未知。"), 25).ToImmutableArray() })));
    }

    [Fact]
    public void 角色说法梦境与时间线索独立保存()
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0];
        var output = Output() with { Findings = [Output().Findings[0] with { Narration = NarrativeSource.Dream, Kind = AnalysisStatementKind.Uncertain, TimeHint = "前一天" }] };
        var result = new ChunkAnalysisContract(input.Source, chunk, Guid.NewGuid(), "stamp", ChunkAnalysisContract.Passages(input.Source, chunk)).Read(Json(output));
        Assert.Equal(NarrativeSource.Dream, result.Findings[0].Narration);
        Assert.Equal("前一天", result.Findings[0].TimeHint);
        Assert.Equal(AnalysisStatementKind.Uncertain, result.Findings[0].Kind);
    }

    [Fact]
    public async Task 单元分析经过共享请求账本且不隐式重试()
    {
        await using var workspace = new TestWorkspace(); var input = ReferenceSourceTests.Create();
        var preset = new ModelPreset("deepseek-v4-flash", 8192, "low");
        var connection = await workspace.Connections.SaveAsync(null, new("提取测试", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
        var calls = 0;
        var model = new ScriptedTextModel(request =>
        {
            calls++; Assert.Contains("Passages", request.UserPrompt); Assert.NotNull(request.Contract);
            return new TextModelResponse(Json(Output()), ModelCompletion.Complete, new(100, 100));
        });
        var requests = new ModelRequestService(model, new ModelRequestStore(workspace.Paths));
        var service = new NovelChunkAnalysisService(workspace.Connections, requests); var budget = new RequestBudget(Guid.NewGuid(), 2, 150000);
        var result = await service.AnalyzeAsync(input, input.Chunks[0].Id, ConnectionService.Bind(connection), Guid.NewGuid(), budget, default);
        Assert.NotEmpty(result.Findings); Assert.Equal(1, calls);
        Assert.Equal(RequestState.Completed, Assert.Single(requests.List(budget.Id)).State);
        var broken = new NovelChunkAnalysisService(workspace.Connections, new(new ScriptedTextModel(_ => throw new IOException("断流")), new ModelRequestStore(workspace.Paths)));
        await Assert.ThrowsAsync<ModelRequestException>(() => broken.AnalyzeAsync(input, input.Chunks[0].Id, ConnectionService.Bind(connection), Guid.NewGuid(), budget, default));
        Assert.Equal(2, requests.List(budget.Id).Count);
        Assert.Contains(requests.List(budget.Id), entry => entry.State == RequestState.Uncertain);
    }

    [Fact]
    public void 大上下文的段落切分不会切断代理对()
    {
        var input = ReferenceSourceTests.Create(new string('甲', 599) + "😀" + new string('乙', 900));
        var passages = ChunkAnalysisContract.Passages(input.Source, input.Chunks[0]);
        Assert.Equal(input.Source.Text, string.Concat(passages.Select(p => p.Text)));
        Assert.All(passages, passage => new SourceRange(passage.Start, passage.Text.Length).Validate(input.Source.Text));
    }
}
