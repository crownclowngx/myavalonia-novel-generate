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

public sealed class AnalysisIntegrationTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
    private static IndexedMention Mention(int number, string name, string description, params string[] aliases) => new(number, 1,
        new(Guid.NewGuid(), name, StoryEntityKind.Person, [.. aliases], description, []));
    private static IndexedFinding Fact(int number, NarrativeSource narration = NarrativeSource.Narration, AnalysisStatementKind certainty = AnalysisStatementKind.Explicit) => new(number, 1,
        new(Guid.NewGuid(), AnalysisDimension.World, "钟楼", number == 1 ? "钟楼禁止外人进入。" : "持令牌的邮差可以进入。", certainty, []) { Narration = narration });
    private static ContinuityObservation Observation(params int[] ids) => new("钟楼", "持令牌的邮差属于进入禁令的例外。", AnalysisStatementKind.Inferred, NarrativeSource.Unknown,
        "正文当前时间", ContinuityRole.Exception, "持有令牌", "邮差可进入", [.. ids]);

    [Fact]
    public void 别名可归并而同名异人保留独立映射()
    {
        var input = new[] { Mention(1, "林远", "邮差", "小林"), Mention(2, "小林", "送信人", "林远"), Mention(3, "林远", "老医生") };
        var output = new IdentityOutput([new("林远（邮差）", StoryEntityKind.Person, [1, 2], AnalysisStatementKind.Inferred, "别名及送信职责相符。"),
            new("林远（医生）", StoryEntityKind.Person, [3], AnalysisStatementKind.Explicit, "同名但年龄职业不同。")], []);
        var result = new IdentityAnalysisContract(input).Read(Json(output)); Assert.Equal(2, result.Groups.Length);
        Assert.Equal(new[] { 1, 2 }, result.Groups[0].Mentions.ToArray()); Assert.Equal(new[] { 3 }, result.Groups[1].Mentions.ToArray());
    }

    [Fact]
    public void 身份遗漏保留未知单例重复越界及跨类别归并均拒绝()
    {
        var input = new[] { Mention(1, "林远", "邮差"), Mention(2, "林远", "医生") };
        var contract = new IdentityAnalysisContract(input); var output = new IdentityOutput([new("林远", StoryEntityKind.Person, [1], AnalysisStatementKind.Uncertain, "无法确认。")], []);
        Assert.Equal(2, contract.Read(Json(output)).Groups.Length); Assert.Equal(AnalysisStatementKind.Uncertain, contract.Read(Json(output)).Groups[1].Certainty);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Groups = [output.Groups[0], output.Groups[0]] })));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Groups = [output.Groups[0] with { Mentions = [99] }] })));
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Groups = [output.Groups[0] with { Category = StoryEntityKind.Place }] })));
    }

    [Fact]
    public async Task 不确定多提及分组不自动变成同一身份()
    {
        await using var workspace = new TestWorkspace(); var (service, run, store) = await Setup(workspace, new Router());
        var index = new NovelAnalysisIndex([Mention(1, "林远", "邮差"), Mention(2, "林远", "医生")], [], []);
        var node = new AnalysisNode("identity-1", AnalysisNodeKind.Integration, null, [], Guid.NewGuid(), new('a', 64), AnalysisNodeState.Completed) { Selection = [1, 2] };
        var output = Json(new IdentityOutput([new("林远", StoryEntityKind.Person, [1, 2], AnalysisStatementKind.Uncertain, "身份仍冲突。")], []));
        var raw = new AnalysisNodeResult(node.Key, node.InputStamp, output, AnalysisLimits.HashText(output));
        var snapshot = NovelIntegrationSnapshot.Build(run with { Nodes = [node] }, index, new Dictionary<string, AnalysisNodeResult> { [node.Key] = raw });
        Assert.Equal(2, snapshot.Identities.Length); Assert.All(snapshot.Identities, i => Assert.Single(i.Mentions));
        Assert.All(snapshot.Identities, i => Assert.Single(i.PossibleSameMentions)); Assert.NotEmpty(snapshot.OpenQuestions);
        Assert.Equal(snapshot.Identities[0].Id, NovelIntegrationSnapshot.Build(run with { Nodes = [node] }, index, new Dictionary<string, AnalysisNodeResult> { [node.Key] = raw }).Identities[0].Id);
    }

    [Theory]
    [InlineData(NarrativeSource.Dream)]
    [InlineData(NarrativeSource.Rumor)]
    [InlineData(NarrativeSource.CharacterClaim)]
    [InlineData(NarrativeSource.Plan)]
    public void 非客观来源不允许无条件升级确定事实(NarrativeSource narration)
    {
        var contract = new ContinuityAnalysisContract([Fact(1, narration)]);
        var output = new ContinuityOutput([Observation(1) with { Certainty = AnalysisStatementKind.Explicit, Narration = NarrativeSource.Narration }], []);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output)));
        var preserved = output with { Observations = [output.Observations[0] with { Certainty = AnalysisStatementKind.Uncertain, Narration = narration }] };
        Assert.Equal(narration, contract.Read(Json(preserved)).Observations[0].Narration);
    }

    [Fact]
    public void 规则例外与冲突保留条件并引用双方而非覆盖旧结论()
    {
        var contract = new ContinuityAnalysisContract([Fact(1), Fact(2)]);
        var output = new ContinuityOutput([Observation(1, 2)], ["令牌是否有期限尚不明确。"]);
        var result = contract.Read(Json(output)); Assert.Equal("持有令牌", result.Observations[0].Conditions); Assert.Equal(2, result.Observations[0].Facts.Length);
        var conflict = output with { Observations = [Observation(1, 2) with { Role = ContinuityRole.Contradiction, Certainty = AnalysisStatementKind.Explicit }] };
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(conflict)));
        Assert.Equal(AnalysisStatementKind.Uncertain, contract.Read(Json(conflict with { Observations = [conflict.Observations[0] with { Certainty = AnalysisStatementKind.Uncertain }] })).Observations[0].Certainty);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Observations = [Observation(99)] })));
    }

    [Fact]
    public void 稠密输入的多条独立观察保留且数量仍有硬上限()
    {
        var facts = Enumerable.Range(1, 60).Select(i => Fact(i)).ToArray(); var contract = new ContinuityAnalysisContract(facts);
        var output = new ContinuityOutput([.. Enumerable.Range(1, 42).Select(i => Observation(i))], []);
        Assert.Equal(42, contract.Read(Json(output)).Observations.Length);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Observations = [.. Enumerable.Range(0, 81).Select(_ => Observation(1))] })));
    }

    [Fact]
    public void 已明确存在的价值冲突与未消解的事实矛盾分别表达()
    {
        var contract = new ContinuityAnalysisContract([Fact(1, NarrativeSource.CharacterClaim)]);
        var output = new ContinuityOutput([Observation(1) with { Role = ContinuityRole.Conflict, Certainty = AnalysisStatementKind.Explicit, Narration = NarrativeSource.CharacterClaim }], []);
        Assert.Equal(ContinuityRole.Conflict, contract.Read(Json(output)).Observations[0].Role);
        Assert.Throws<InvalidDataException>(() => contract.Read(Json(output with { Observations = [output.Observations[0] with { Role = ContinuityRole.Contradiction }] })));
    }

    [Fact]
    public void 倒叙和目标结果角色保留独立叙述时间()
    {
        var contract = new ContinuityAnalysisContract([Fact(1, NarrativeSource.Recollection), Fact(2)]);
        var output = new ContinuityOutput([Observation(1) with { Role = ContinuityRole.Goal, Narration = NarrativeSource.Recollection, StoryTime = "三年前" },
            Observation(2) with { Role = ContinuityRole.Outcome, StoryTime = "正文当前时间" }], ["离开前立下的目标是否彻底完成仍待确认。"]);
        var result = contract.Read(Json(output)); Assert.Equal("三年前", result.Observations[0].StoryTime); Assert.Equal(ContinuityRole.Outcome, result.Observations[1].Role);
        Assert.Equal(NarrativeSource.Recollection, result.Observations[0].Narration);
    }

    [Fact]
    public async Task 整合节点自动追加且重开与复用不重复发送()
    {
        await using var workspace = new TestWorkspace(); var model = new Router(); var (service, run, store) = await Setup(workspace, model);
        var completed = await service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.IntegrationCompleted, completed.State); Assert.Equal(9, model.Requests.Count);
        var integrated = service.ReadIntegrated(run.Id); Assert.Single(integrated.Identities); Assert.Equal(6, integrated.Observations.Length);
        Assert.Empty(integrated.UnintegratedFacts); Assert.Equal(12, integrated.Index.Findings.Length);
        await service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(9, model.Requests.Count);
        var revision = await service.CreateAsync(run.BookId, ConnectionService.Bind(run.Connection.Connection), 30, 1000000, new(9, 200000), default, previousRunId: run.Id, target: AnalysisTarget.Integration);
        Assert.Equal(AnalysisRunState.IntegrationCompleted, (await service.ExecuteAsync(revision.Id, new(), null, default)).State);
        Assert.Equal(9, model.Requests.Count); Assert.Equal(run.Budget.Id, revision.Budget.Id);
    }

    [Fact]
    public async Task 整合失败沿用账本恢复且原始提取不重发()
    {
        await using var workspace = new TestWorkspace(); var model = new Router { FailIntegrationOnce = true }; var (service, run, store) = await Setup(workspace, model);
        Assert.Equal(AnalysisRunState.NeedsAttention, (await service.ExecuteAsync(run.Id, new(), null, default)).State); Assert.Equal(3, model.Requests.Count);
        await service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(3, model.Requests.Count);
        service.Resume(run.Id, true, 30, 1000000, "保留同名异人，不要强行归并。");
        Assert.Equal(AnalysisRunState.IntegrationCompleted, (await service.ExecuteAsync(run.Id, new(), null, default)).State); Assert.Equal(10, model.Requests.Count);
        Assert.Equal(2, model.Requests.Count(r => r.Contract is ChunkAnalysisContract)); Assert.Equal(10, service.Usage(run.Id).Count);
        Assert.Contains("保留同名异人", model.Requests[3].SystemPrompt);
        var revision = await service.CreateAsync(run.BookId, ConnectionService.Bind(run.Connection.Connection), 30, 1000000, new(9, 200000), default, previousRunId: run.Id, target: AnalysisTarget.Integration);
        Assert.Equal(AnalysisRunState.IntegrationCompleted, (await service.ExecuteAsync(revision.Id, new(), null, default)).State); Assert.Equal(10, model.Requests.Count);
    }

    [Fact]
    public async Task 高密度候选按容量分批且不丢提及与事实()
    {
        await using var workspace = new TestWorkspace(); var (service, run, store) = await Setup(workspace, new Router());
        var mentions = Enumerable.Range(1, 145).Select(i => Mention(i, "师父", new string('甲', 500))).ToImmutableArray();
        var facts = Enumerable.Range(1, 145).Select(i => Fact(i) with { Value = Fact(i).Value with { Statement = new('乙', 3900) } }).ToImmutableArray();
        var plan = NovelIntegrationPlanner.Build(run, new(mentions, facts, []));
        Assert.All(plan, n => Assert.InRange(n.Selection.Length, 1, 60));
        Assert.Equal(Enumerable.Range(1, 145), plan.Where(n => n.Dimension is null).SelectMany(n => n.Selection).Order());
        Assert.Equal(Enumerable.Range(1, 145), plan.Where(n => n.Dimension is not null).SelectMany(n => n.Selection).Order());
        Assert.True(plan.Count(n => n.Dimension is not null) > 3);
    }

    [Fact]
    public async Task 未完成前置节点不能提前追加下一阶段()
    {
        await using var workspace = new TestWorkspace(); var (service, run, store) = await Setup(workspace, new Router());
        var extra = new AnalysisNode("identity-1", AnalysisNodeKind.Integration, null, [run.Nodes[0].Key], Guid.NewGuid(), "", AnalysisNodeState.Pending) { Selection = [1] };
        Assert.Throws<InvalidDataException>(() => store.Save(run with { Nodes = run.Nodes.Add(extra), Version = 2 }, 1));
        Assert.Throws<InvalidOperationException>(() => service.ReadIntegrated(run.Id));
    }

    private static async Task<(NovelAnalysisRunService Service, AnalysisRun Run, AnalysisRunStore Store)> Setup(TestWorkspace workspace, ITextModel model)
    {
        var sourceStore = new ReferenceSourceStore(workspace.Paths); var runStore = new AnalysisRunStore(workspace.Paths);
        var input = ReferenceSourceTests.Create("第1章\n林远来到雾港。\n第2章\n林远打开城门。\n");
        sourceStore.Import(NovelTextPartitioner.Partition(input.Source, "跨章夹具", new(6000, 0), default));
        var preset = new ModelPreset("deepseek-flash", 16384, "none");
        var connection = await workspace.Connections.SaveAsync(null, new("整合测试", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
        var service = new NovelAnalysisRunService(sourceStore, runStore, workspace.Connections, new(model, new ModelRequestStore(workspace.Paths)), new NovelAnalysisNodePreparer());
        var run = await service.CreateAsync(input.Book.Id, ConnectionService.Bind(connection), 30, 1000000, new(9, 200000), default, target: AnalysisTarget.Integration);
        return (service, run, runStore);
    }

    private sealed class Router : ITextModel
    {
        public List<TextModelRequest> Requests { get; } = [];
        public bool FailIntegrationOnce { get; set; }
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); string json;
            if (request.Contract is ChunkAnalysisContract)
            {
                var basic = NovelChunkAnalysisTests.Output();
                json = Json(basic with { Findings = [.. Enum.GetValues<AnalysisDimension>().Select(d => basic.Findings[0] with { Dimension = d })], Gaps = [] });
            }
            else
            {
                if (FailIntegrationOnce) { FailIntegrationOnce = false; throw new IOException("整合时断流"); }
                using var prompt = JsonDocument.Parse(request.UserPrompt); var items = prompt.RootElement.GetProperty("Items").EnumerateArray().ToArray();
                var ids = items.Select(i => i.GetProperty("Number").GetInt32()).ToImmutableArray();
                json = request.Contract is IdentityAnalysisContract
                    ? Json(new IdentityOutput([new("林远", StoryEntityKind.Person, ids, AnalysisStatementKind.Inferred, "跨章称呼及身份一致。")], []))
                    : Json(new ContinuityOutput([Observation([.. ids])], []));
            }
            return Task.FromResult(new TextModelResponse(json, ModelCompletion.Complete, new(100, 100)));
        }
    }
}
