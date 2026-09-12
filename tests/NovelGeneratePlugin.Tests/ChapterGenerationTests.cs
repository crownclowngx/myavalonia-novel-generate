using NovelGeneratePlugin.Application.Projects;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class ChapterGenerationTests
{
    internal static string Body => "林舟推开旧邮局的门，发现铜钥匙。" + new string('雨', 82);
    internal static ChapterReview Review => new(true, true, true, true, true, "林舟进入旧邮局，找到铜钥匙。", [], []);
    internal static string Json<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
    internal static TextModelResponse Response(string text) => new(text, ModelCompletion.Complete, new(100, 100));
    internal static async Task<BookProject> BookAsync(TestWorkspace workspace)
    {
        var book = BookProject.Create("雾港来信", "邮差调查自己的死亡预告。");
        book = PlanningRules.Apply(book, new(book.Id, book.Chapters[0].Id, PlanningRules.SourceStamp(book), Guid.NewGuid(), "夹具", PlanningTests.Proposal()));
        var preset = new ModelPreset("gpt-6-astra", 8192, "low");
        var connection = await workspace.Connections.SaveAsync(null, new("模型夹具", ModelProvider.CodexCli, "", @"C:\unit\codex.exe", preset, preset, preset));
        return book with { Connection = ConnectionService.Bind(connection) };
    }
    private static ChapterGenerationService Service(TestWorkspace workspace, ITextModel model) => new(workspace.Connections, new(model, new ModelRequestStore(workspace.Paths)), new ChapterWorkStore(workspace.Paths));
    private static Task<ChapterWork> Generate(TestWorkspace workspace, BookProject book, ITextModel model, int repairs = 0, CancellationToken ct = default)
        => Service(workspace, model).GenerateAsync(book, book.Chapters[0].Id, book.Revisions.ActiveRunId ?? Guid.NewGuid(), 100, repairs, new(Guid.NewGuid(), 6, 500000), null, ct);
    [Fact]
    public async Task 单章检查通过后正文摘要事实原子提交并可重开()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var fact = new ReviewedFact(book.Story.Entities[0].Id, StoryFactKind.State, "地点", null, "旧邮局", "推开旧邮局的门", StoryFactTime.Established, null);
        var fake = new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review with { Facts = [fact] })));
        var work = await Generate(workspace, book, fake); Assert.Equal(ChapterWorkState.Ready, work.State); Assert.Single(work.Facts); Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(ModelTask.Drafting, fake.Requests[0].Configuration.Task); Assert.Equal(ModelTask.Checking, fake.Requests[1].Configuration.Task);
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        await session.CommitGeneratedChapterAsync(work); await session.CommitGeneratedChapterAsync(work);
        var stored = workspace.Store.Read(session.Path).Project; var revision = Assert.Single(stored.Revisions.History);
        Assert.Equal(Body, stored.Chapters[0].Text); Assert.Equal(RevisionCheck.Passed, revision.Check); Assert.Null(stored.Revisions.Head(work.ChapterId).FormalId);
        Assert.Equal("旧邮局", Assert.Single(revision.Facts).NewValue); Assert.Equal(Body, new ChapterWorkStore(workspace.Paths).Get(work.Id)!.Text);
    }
    [Theory]
    [InlineData("incomplete")]
    [InlineData("uncertain")]
    [InlineData("evidence")]
    [InlineData("locked")]
    [InlineData("length")]
    [InlineData("planned")]
    [InlineData("unknown")]
    public async Task 必要检查证据字数与事实问题阻止提交(string scenario)
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace); var review = Review; var text = Body;
        review = scenario switch
        {
            "incomplete" => review with { FactsChecked = false },
            "uncertain" => review with { Issues = [new(ReviewSeverity.Uncertain, ReviewCategory.Fact, "未知事件", "铜钥匙")] },
            "evidence" => review with { Issues = [new(ReviewSeverity.Advice, ReviewCategory.Style, "不存在的引文", "海上日出")] },
            "locked" => review with { LockedPlanPreserved = false },
            "planned" or "unknown" => review with { Facts = [new(scenario == "unknown" ? Guid.NewGuid() : book.Story.Entities[0].Id, StoryFactKind.State, "地点", null, "邮局", "旧邮局", scenario == "planned" ? StoryFactTime.Planned : StoryFactTime.Established, null)] },
            _ => review
        };
        if (scenario == "length") text = "太短";
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(text), _ => Response(Json(review))));
        Assert.Equal(ChapterWorkState.NeedsAttention, work.State); Assert.NotEmpty(work.Issues); Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Commit(book, work));
    }
    [Fact]
    public async Task 本地硬规则不能被模型宣称通过所覆盖()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        book = WritingRuleSet.Commit(book, new(null, "禁止铜钥匙", "", WritingRuleKind.ForbiddenText, WritingRuleScope.Book, WritingRuleStrength.Hard, "铜钥匙", false, "", "作者", true), book.Chapters[0].Id);
        book = PlanningRules.MarkReady(book, book.Chapters[0].Id);
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review))));
        Assert.Equal(ChapterWorkState.NeedsAttention, work.State); Assert.Contains(work.Issues, i => i.Severity == ReviewSeverity.Hard);
    }
    [Fact]
    public async Task 修正后重新全套检查且保留修正前后版本()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var failed = Review with { Issues = [new(ReviewSeverity.Hard, ReviewCategory.Style, "换成铁钥匙", "铜钥匙")] };
        var fake = new ScriptedTextModel(_ => Response(Body), _ => Response(Json(failed)), _ => Response(Json(new ChapterPatches([new("铜钥匙", "铁钥匙")]))), _ => Response(Json(Review)));
        var work = await Generate(workspace, book, fake, 1); Assert.Equal(ChapterWorkState.Ready, work.State); Assert.Equal(4, fake.Requests.Count);
        Assert.Equal(2, work.Attempts.Length); Assert.Contains("铜钥匙", work.Attempts[0].Text); Assert.Contains("铁钥匙", work.Text); Assert.Contains("铁钥匙", fake.Requests[3].UserPrompt);
    }
    [Fact]
    public async Task 修正引入新事实疑点即使修正次数耗尽也不提交()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var fake = new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review with { LockedPlanPreserved = false })),
            _ => Response(Json(new ChapterPatches([new("铜钥匙", "铁钥匙")]))), _ => Response(Json(Review with { Issues = [new(ReviewSeverity.Uncertain, ReviewCategory.Fact, "材质与前文冲突", "铁钥匙")] })));
        var work = await Generate(workspace, book, fake, 1); Assert.Equal(ChapterWorkState.NeedsAttention, work.State); Assert.Equal(4, fake.Requests.Count);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("large")]
    public void 局部修正必须唯一有界且不会猜测位置(string scenario)
    {
        var patch = scenario switch { "missing" => new ChapterPatch("不存在", "改文"), "duplicate" => new("雨", "雪"), _ => new ChapterPatch(Body, "新正文") };
        Assert.ThrowsAny<Exception>(() => ChapterGenerationRules.ApplyPatches(Body, new([patch])));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 正文或规范变更后审校候选不能覆盖当前作品(bool text)
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review))));
        var changed = text ? book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "作者的新输入" }) } : book with { Profile = book.Profile with { World = "新的锁定世界" } };
        Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Commit(changed, work)); Assert.Empty(changed.Revisions.History);
    }
    [Fact]
    public async Task 截断生成和不合法审校均保留正文但不能误报完成()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var truncated = new ScriptedTextModel(_ => new(Body, ModelCompletion.Truncated, new(10, 20)));
        var work = await Generate(workspace, book, truncated); Assert.Equal(ChapterWorkState.NeedsAttention, work.State); Assert.Single(truncated.Requests); Assert.Null(work.Review);
        work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response("{\"Summary\":\"缺少必要字段\"}")));
        Assert.Equal(ChapterWorkState.Failed, work.State); Assert.Equal(Body, new ChapterWorkStore(workspace.Paths).Get(work.Id)!.Text);
    }
    [Fact]
    public async Task 本地提交中断保持作者正文与修订原样且候选可重试()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review))));
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        using (var connection = ProjectStore.Connect(session.Path)) { using var cmd = connection.CreateCommand(); cmd.CommandText = "CREATE TRIGGER reject_update BEFORE UPDATE ON project BEGIN SELECT RAISE(ABORT, '测试事务中断'); END;"; cmd.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<SqliteException>(() => session.CommitGeneratedChapterAsync(work)); Assert.Equal(book.Chapters[0].Text, session.Current.Chapters[0].Text); Assert.Empty(workspace.Store.Read(session.Path).Project.Revisions.History);
        using (var connection = ProjectStore.Connect(session.Path)) { using var cmd = connection.CreateCommand(); cmd.CommandText = "DROP TRIGGER reject_update"; cmd.ExecuteNonQuery(); }
        await session.CommitGeneratedChapterAsync(new ChapterWorkStore(workspace.Paths).Get(work.Id)!); Assert.Single(workspace.Store.Read(session.Path).Project.Revisions.History);
    }
    [Fact]
    public async Task 取消后迟到完整结果不得进入审校或作品()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace); using var cancellation = new CancellationTokenSource();
        var model = new LateModel(); var task = Generate(workspace, book, model, ct: cancellation.Token); await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel(); model.Completed.SetResult(Response(Body)); var work = await task;
        Assert.Equal(ChapterWorkState.Cancelled, work.State); Assert.Equal(Body, work.Text); Assert.Null(work.Review); Assert.Empty(book.Revisions.History);
    }
    private sealed class LateModel : ITextModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TextModelResponse> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken) { Started.SetResult(); return Completed.Task; }
    }
    [Fact]
    public void 重叠原文不能误判为唯一补丁位置()
    {
        Assert.Throws<InvalidDataException>(() => ChapterGenerationRules.ApplyPatches("aaa" + new string('b', 97), new([new("aa", "X")])));
    }
    [Fact]
    public async Task 较旧候选快照不能覆盖新序列和任务身份()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review)))); var store = new ChapterWorkStore(workspace.Paths);
        Assert.Throws<InvalidOperationException>(() => store.Save(work with { Text = "旧快照", UpdateSequence = work.UpdateSequence - 1 }));
        Assert.Throws<InvalidOperationException>(() => store.Save(work with { RunId = Guid.NewGuid(), UpdateSequence = work.UpdateSequence + 1 }));
        Assert.Equal(Body, store.Get(work.Id)!.Text);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 生成提交并发编辑按字段合并且不倒退生成正文(bool editBody)
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review))));
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PausingStore(workspace.Store, entered, release);
        await using var sessions = new ProjectSessions(store, workspace.Catalog, workspace.Recovery, new FileProjectLeaseProvider());
        await using var session = await sessions.CreateAsync(workspace.ProjectPath(), book);
        var committing = session.CommitGeneratedChapterAsync(work);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var current = session.Current with { Title = "作者新书名" };
            if (editBody) current = current with { Chapters = current.Chapters.SetItem(0, current.Chapters[0] with { Text = "作者的新正文" }) };
            session.Update(current);
        }
        finally { release.Set(); }
        await committing; Assert.True((await session.SaveAsync()).Saved); var reopened = workspace.Store.Read(session.Path).Project;
        Assert.Equal("作者新书名", reopened.Title); Assert.Equal(editBody ? "作者的新正文" : Body, reopened.Chapters[0].Text); Assert.Equal(Body, Assert.Single(reopened.Revisions.History).Text);
    }
    [Fact]
    public async Task 保存状态订阅异常不能终止自动保存或阻止租约释放()
    {
        await using var workspace = new TestWorkspace(); var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("通知测试"));
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, _) => { if (session.Status.State == SaveState.Saving) throw new InvalidOperationException("测试显示失败"); };
        session.StateChanged += (_, _) => { if (session.Status.State == SaveState.Saved) saved.TrySetResult(); };
        session.Update(session.Current with { Title = "自动保存未被显示异常阻断" }); await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("自动保存未被显示异常阻断", workspace.Store.Read(session.Path).Project.Title); await session.DisposeAsync();
        await using var reopened = await workspace.Sessions.OpenAsync(workspace.ProjectPath()); Assert.Equal("自动保存未被显示异常阻断", reopened.Current.Title);
    }
    private sealed class PausingStore(IProjectStore inner, TaskCompletionSource entered, ManualResetEventSlim release) : IProjectStore
    {
        private int writes;
        public StoredProject Create(string path, BookProject book) => inner.Create(path, book);
        public StoredProject Read(string path) => inner.Read(path);
        public long Save(string path, BookProject book, long version)
        { if (Interlocked.Increment(ref writes) == 1) { entered.SetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } return inner.Save(path, book, version); }
    }

    [Fact]
    public async Task 审校只修复外围格式且原始报告留在账本()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var wrapped = "```json\n" + Json(Review) + "\n```";
        var work = await Generate(workspace, book, new ScriptedTextModel(_ => Response(Body), _ => Response(wrapped)));
        Assert.Equal(ChapterWorkState.Ready, work.State);
        var raw = new ModelRequestStore(workspace.Paths).Recent(book.Connection!.ConnectionId);
        Assert.Contains(raw, r => r.PartialText == wrapped);
        Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(ModelRequestService.RepairJsonWrapper("```json\n{\"Summary\":\"截断"), new ChapterReviewContract()));
    }

    [Fact]
    public async Task 未通过的前文工作稿不能作为自动续写入口()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace); var run = Guid.NewGuid();
        var first = book.Chapters[0]; book = book with { Chapters = book.Chapters.SetItem(0, first with { Text = Body }) };
        var submission = new DraftSubmission(book.Id, first.Id, null, null, RevisionRules.Hash(Body), RevisionRules.ContextStamp(book, first.Id), Body, "手工未检查稿", [], RevisionCheck.NotChecked, run, Guid.NewGuid());
        book = book with { Revisions = RevisionRules.CommitWorking(book, submission) };
        Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Preflight(book, book.Chapters[1].Id, run));
        var fake = new ScriptedTextModel(); await Assert.ThrowsAsync<InvalidOperationException>(() => Service(workspace, fake).GenerateAsync(book, book.Chapters[1].Id, run, 100, 0, new(Guid.NewGuid(), 2, 100000), null, default)); Assert.Empty(fake.Requests);
    }

}
