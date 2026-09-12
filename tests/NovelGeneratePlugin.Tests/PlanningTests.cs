using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class PlanningTests
{
    internal static PlanningProposal Proposal(int count = 3) => new("邮差追查一封写着自己死亡日期的信，最终揭开雾港邮局的秘密。", [new("雾港来信", "找到寄信人与信件来源")],
        Enumerable.Range(1, count).Select(i => new ProposedChapter(1, $"第 {i} 章：旧邮局", "查明信封上的日期", "有人试图抢走信件", "林舟有限视角", $"第 {i} 日，雾港", "林舟调查邮戳，被守夜人阻拦。", "获得一条寄信线索", "铜钥匙的齿纹", "由邮戳线索转向旧书店", [])).ToImmutableArray(),
        [new("林舟", StoryEntityKind.Person, ["阿舟"], "年轻邮差")]);
    private static BookProject Book() => BookProject.Create("雾港来信", "邮差收到一封写着自己死亡日期的信。");
    private static PlanningCandidate Candidate(BookProject book, PlanningProposal? proposal = null) => new(book.Id, book.Chapters[0].Id, PlanningRules.SourceStamp(book), Guid.NewGuid(), "固定规划夹具", proposal ?? Proposal());
    private static readonly JsonSerializerOptions OutputOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static string Output(PlanningProposal proposal) => JsonSerializer.Serialize(proposal, OutputOptions);
    [Fact]
    public void 三章规划保留既有正文与身份并创建可用章纲和历史()
    {
        var book = Book(); book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "作者先写好的开场。" }) };
        var planned = PlanningRules.Apply(book, Candidate(book)); Assert.Equal(3, planned.Chapters.Length);
        Assert.Equal(book.Chapters[0].Id, planned.Chapters[0].Id); Assert.Equal("作者先写好的开场。", planned.Chapters[0].Text); Assert.Equal(book.Chapters[0].Title, planned.Chapters[0].Title);
        Assert.Single(planned.Planning.History); Assert.Single(planned.Story.Entities); Assert.Equal("找到寄信人与信件来源", planned.Volumes[0].Goal);
        foreach (var chapter in planned.Chapters) PlanningRules.RequireReady(planned, chapter.Id);
        Assert.Empty(planned.Revisions.History); Assert.Contains("状态变化（计划）", planned.Chapters[0].Outline);
    }
    [Fact]
    public void 方法应用必须有原文出处和具体事件()
    {
        var book = Book() with { Profile = new("", "", "先建立期待，再制造阻碍，最后兑现。", "") };
        Assert.Throws<InvalidDataException>(() => PlanningRules.Apply(book, Candidate(book)));
        var method = new MethodApplication("建立期待", "信上预告死亡", "林舟无法进入邮局", "他发现秘密入口");
        var proposal = Proposal(); proposal = proposal with { Chapters = proposal.Chapters.Select(c => c with { Methods = [method] }).ToImmutableArray() };
        var planned = PlanningRules.Apply(book, Candidate(book, proposal)); Assert.Contains("秘密入口", planned.Chapters[0].Outline);
        proposal = proposal with { Chapters = proposal.Chapters.SetItem(0, proposal.Chapters[0] with { Methods = [method with { SourceQuote = "伪造的课件原文" }] }) };
        Assert.Throws<InvalidDataException>(() => PlanningRules.Apply(book, Candidate(book, proposal)));
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("count")]
    [InlineData("volume")]
    [InlineData("goal")]
    [InlineData("entity")]
    [InlineData("extra")]
    public void 空结构错章数越界卷缺字段重名和多余字段拒绝(string defect)
    {
        var proposal = Proposal(); string text;
        if (defect == "empty") text = "{}";
        else if (defect == "extra") { var node = JsonNode.Parse(Output(proposal))!; node["Extra"] = true; text = node.ToJsonString(); }
        else
        {
            proposal = defect switch
            {
                "count" => Proposal(4),
                "volume" => proposal with { Chapters = proposal.Chapters.SetItem(0, proposal.Chapters[0] with { Volume = 9 }) },
                "goal" => proposal with { Chapters = proposal.Chapters.SetItem(0, proposal.Chapters[0] with { Goal = "" }) },
                _ => proposal with { Entities = [proposal.Entities[0], proposal.Entities[0]] }
            }; text = Output(proposal);
        }
        Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(text, new PlanningOutputContract(3)));
    }
    [Fact]
    public void 锁定章节和过期候选阻止采用且不删除既有章节()
    {
        var book = Book(); var candidate = Candidate(book);
        Assert.Throws<InvalidOperationException>(() => PlanningRules.Apply(book with { Idea = "作者已改变创意" }, candidate));
        var locked = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Plan = ChapterPlan.Empty with { Locked = true } }) };
        Assert.Throws<InvalidOperationException>(() => PlanningRules.CheckTargets(locked, locked.Chapters[0].Id, 3));
        for (var i = 0; i < 4; i++) book = BookEdits.AddChapter(book, null).Project;
        var last = book.Chapters[^1]; var updated = PlanningRules.Apply(book, Candidate(book)); Assert.Equal(last, updated.Chapters[^1]); Assert.Equal(5, updated.Chapters.Length);
    }
    [Fact]
    public void 局部规划不会把已有章移动到另一卷()
    {
        var book = BookEdits.AddChapter(Book(), null).Project; var proposal = Proposal();
        proposal = proposal with { Volumes = [new("一", "目标一"), new("二", "目标二")], Chapters = proposal.Chapters.SetItem(1, proposal.Chapters[1] with { Volume = 2 }).SetItem(2, proposal.Chapters[2] with { Volume = 2 }) };
        Assert.Throws<InvalidOperationException>(() => PlanningRules.Apply(book, Candidate(book, proposal))); Assert.Single(book.Volumes);
    }
    [Fact]
    public async Task 协作候选持久化重开后仍能采用并拒绝覆盖历史()
    {
        await using var workspace = new TestWorkspace(); var book = Book(); var candidate = Candidate(book);
        book = book with { Planning = book.Planning with { Pending = candidate } }; workspace.Store.Create(workspace.ProjectPath(), book);
        var read = workspace.Store.Read(workspace.ProjectPath()); Assert.NotNull(read.Project.Planning.Pending);
        Assert.Equal(candidate.SourceStamp, PlanningRules.SourceStamp(read.Project)); var applied = PlanningRules.Apply(read.Project, read.Project.Planning.Pending!);
        var version = workspace.Store.Save(workspace.ProjectPath(), applied, read.Version); Assert.Null(applied.Planning.Pending);
        Assert.Throws<InvalidOperationException>(() => workspace.Store.Save(workspace.ProjectPath(), applied with { Planning = PlanningLedger.Empty }, version));
        var copy = applied.CopyAsNew(); Assert.NotEqual(applied.Id, copy.Id); Assert.NotEqual(applied.Planning.History[0].Chapters[0].ChapterId, copy.Planning.History[0].Chapters[0].ChapterId);
        PlanningRules.RequireReady(copy, copy.Chapters[0].Id); copy.Validate();
    }
    [Fact]
    public void 手工改稿先作为草案且规范变化使规划门禁过期()
    {
        var book = Book(); book = PlanningRules.Apply(book, Candidate(book)); var chapter = book.Chapters[0];
        book = book with { Chapters = book.Chapters.SetItem(0, chapter with { Plan = chapter.Plan with { Conflict = "新冲突" } }) };
        Assert.Throws<InvalidOperationException>(() => PlanningRules.RequireReady(book, chapter.Id));
        book = PlanningRules.Record(PlanningRules.MarkReady(book, chapter.Id), Guid.NewGuid(), "手工修订"); Assert.Equal(2, book.Planning.History.Length);
        PlanningRules.RequireReady(book, chapter.Id);
        book = book with { Profile = book.Profile with { World = "新的世界约束" } };
        Assert.Throws<InvalidOperationException>(() => PlanningRules.RequireReady(book, chapter.Id));
    }
    [Theory]
    [InlineData(PlanningMode.Automatic, true)]
    [InlineData(PlanningMode.Collaborative, false)]
    public async Task 原生文档命令的自动和协作模式共用规划服务(PlanningMode mode, bool applied)
    {
        await using var workspace = new TestWorkspace(); var preset = new ModelPreset("gpt-6-astra", 8192, "high");
        var connection = await workspace.Connections.SaveAsync(null, new("测试", ModelProvider.CodexCli, "", @"C:\unit\codex.exe", preset, preset, preset));
        var fake = new ScriptedTextModel(_ => new(Output(Proposal()), ModelCompletion.Complete, new(100, 200)));
        var service = new PlanningService(workspace.Connections, new(fake, new ModelRequestStore(workspace.Paths)));
        await using var document = workspace.CreateDocument(service); await document.InitializeAsync(new NewDocumentActivation("规划"), default);
        document.Idea = "邮差收到死亡预告信"; workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.SelectedBookConnection = document.ConnectionChoices.Single(c => c.Id == connection.Id); await document.BindConnectionCommand.ExecuteAsync(null);
        document.SelectedPlanningMode = mode; await document.GeneratePlanningCommand.ExecuteAsync(null);
        Assert.Single(fake.Requests); var book = workspace.Store.Read(document.ProjectPath).Project;
        Assert.Equal(applied ? 3 : 1, book.Chapters.Length); Assert.Equal(!applied, book.Planning.Pending is not null);
        if (!applied) await document.ApplyPlanningCommand.ExecuteAsync(null);
        Assert.Equal(3, document.Chapters.Count); Assert.Contains("通过", document.PlanningStatus);
        document.PlanGoal = "作者修改本章目标"; await document.SaveChapterPlanCommand.ExecuteAsync(null);
        Assert.Equal(2, workspace.Store.Read(document.ProjectPath).Project.Planning.History.Length);
        document.AddPlanMethodCommand.Execute(null); Assert.Single(document.PlanMethods); document.PlanMethods[0].RemoveCommand.Execute(null); Assert.Empty(document.PlanMethods);
    }
    [Fact]
    public void 主线与卷目标必须进入上下文且修改后旧预览过期()
    {
        var book = Book(); book = PlanningRules.Apply(book, Candidate(book));
        var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, null, false);
        Assert.Contains(book.Planning.Mainline, context.Render()); Assert.Contains(book.Volumes[0].Goal, context.Render());
        var changed = book with { Planning = book.Planning with { Mainline = "新的主线" } };
        Assert.NotEqual(context.Stamp, StoryMemory.Stamp(changed, book.Chapters[0].Id, null, false));
        changed = book with { Volumes = book.Volumes.SetItem(0, book.Volumes[0] with { Goal = "新的卷目标" }) };
        Assert.NotEqual(context.Stamp, StoryMemory.Stamp(changed, book.Chapters[0].Id, null, false));
    }
    [Fact]
    public async Task 截断响应保留请求候选且不产生可采用规划()
    {
        await using var workspace = new TestWorkspace(); var preset = new ModelPreset("gpt-6-astra", 8192, "low");
        var connection = await workspace.Connections.SaveAsync(null, new("测试", ModelProvider.CodexCli, "", @"C:\unit\codex.exe", preset, preset, preset));
        var book = Book() with { Connection = ConnectionService.Bind(connection) };
        var store = new ModelRequestStore(workspace.Paths); var budget = new RequestBudget(Guid.NewGuid(), 1, 250000);
        var service = new PlanningService(workspace.Connections, new(new ScriptedTextModel(_ => new(Output(Proposal()), ModelCompletion.Truncated, new(100, 200))), store));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(book, book.Chapters[0].Id, 3, budget, default));
        Assert.NotEmpty(Assert.Single(store.List(budget.Id)).PartialText); Assert.Empty(book.Planning.History); Assert.Null(book.Planning.Pending);
    }
    [Fact]
    public async Task 关闭预检通过后其他工具否决时作品仍可编辑保存()
    {
        await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("关闭预检"), default);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        Assert.True(await document.SaveBeforeCloseAsync());
        document.ChapterText = "作者在关闭被取消后继续写作"; await document.SaveCommand.ExecuteAsync(null);
        Assert.Equal(document.ChapterText, workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Text);
    }

    [Fact]
    public async Task 取消与完成同时到达时不写入作品规划()
    {
        await using var workspace = new TestWorkspace(); var preset = new ModelPreset("gpt-6-astra", 8192, "low");
        var connection = await workspace.Connections.SaveAsync(null, new("测试", ModelProvider.CodexCli, "", @"C:\unit\codex.exe", preset, preset, preset));
        var delayed = new DelayedPlanningModel();
        var planning = new PlanningService(workspace.Connections, new(delayed, new ModelRequestStore(workspace.Paths)));
        await using var document = workspace.CreateDocument(planning); await document.InitializeAsync(new NewDocumentActivation("取消规划"), default);
        document.Idea = "邮差收到死亡预告信"; workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.SelectedBookConnection = document.ConnectionChoices.Single(c => c.Id == connection.Id); await document.BindConnectionCommand.ExecuteAsync(null);
        var operation = document.GeneratePlanningCommand.ExecuteAsync(null); await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(document.CancelPlanningCommand.CanExecute(null)); document.CancelPlanningCommand.Execute(null);
        delayed.Completed.SetResult(new(Output(Proposal()), ModelCompletion.Complete, new(100, 200))); await operation;
        var book = workspace.Store.Read(document.ProjectPath).Project; Assert.Empty(book.Planning.History); Assert.Null(book.Planning.Pending); Assert.Single(book.Chapters);
    }
    // 故意模拟服务商在取消后才返回完整结果，验证 UI 的采用边界不能依赖适配器恰好抛出取消异常。
    private sealed class DelayedPlanningModel : ITextModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TextModelResponse> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        { Started.SetResult(); return Completed.Task; }
    }

    [Fact]
    public async Task 第六版作品备份升级且旧事实策略指纹保持稳定()
    {
        await using var workspace = new TestWorkspace(); var book = Book(); var stamp = StoryMemory.PolicyStamp(book, book.Chapters[0].Id, null);
        workspace.Store.Create(workspace.ProjectPath(), book);
        using (var connection = ProjectStore.Connect(workspace.ProjectPath(), Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE project SET snapshot=json_remove(snapshot,'$.Planning','$.Chapters[0].Plan','$.Volumes[0].Goal'); PRAGMA user_version=6;";
            command.ExecuteNonQuery();
        }
        var migrated = workspace.Store.Read(workspace.ProjectPath()).Project;
        Assert.Empty(migrated.Planning.History); Assert.Equal(stamp, StoryMemory.PolicyStamp(migrated, migrated.Chapters[0].Id, null));
        Assert.Single(Directory.GetFiles(workspace.Root, "*.before-v7-*.noveldb"));
        Assert.Throws<InvalidDataException>(() => PlanningRules.RequireReady(migrated, migrated.Chapters[0].Id));
    }

}
