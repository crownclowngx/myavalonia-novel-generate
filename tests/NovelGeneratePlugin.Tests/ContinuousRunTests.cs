using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class ContinuousRunTests
{
    private static RunPolicy Policy(int maximumRequests = 20, long maximumTokens = 1000000, bool plan = false) => new(3, 100, 0, maximumRequests, maximumTokens, plan);
    private static ScriptedTextModel Success(int chapters) => new(Enumerable.Range(0, chapters * 2).Select<int, Func<TextModelRequest, TextModelResponse>>(i => _ => ChapterGenerationTests.Response(i % 2 == 0 ? ChapterGenerationTests.Body : ChapterGenerationTests.Json(ChapterGenerationTests.Review))).ToArray());
    private static ContinuousRunService Service(TestWorkspace workspace, ITextModel model, IContinuousRunStore? runs = null)
    {
        var requests = new ModelRequestService(model, new ModelRequestStore(workspace.Paths)); var work = new ChapterWorkStore(workspace.Paths);
        return new(new(workspace.Connections, requests), new(workspace.Connections, requests, work), requests, work, runs ?? new ContinuousRunStore(workspace.Paths));
    }
    [Fact]
    public async Task 三章连续检查提交自动承接前文且共享预算()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var service = Service(workspace, fake); var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, run.State); Assert.Equal(3, run.NextChapter); Assert.Equal(6, fake.Requests.Count);
        var saved = workspace.Store.Read(session.Path).Project; Assert.Equal(3, saved.Revisions.History.Length); Assert.All(saved.Revisions.History, r => Assert.Equal(run.StoryRunId, r.RunId));
        Assert.All(saved.Revisions.Heads, h => Assert.Null(h.FormalId)); Assert.Contains(ChapterGenerationTests.Review.Summary, fake.Requests[2].UserPrompt);
        Assert.Equal(6, service.Usage(run).Count); Assert.Single(service.Usage(run).Select(r => r.BudgetId).Distinct());
        Assert.Equal(ContinuousRunState.Completed, (await service.LoadAsync(book.Id))!.State);
    }
    [Fact]
    public async Task 当前章结束后暂停且明确继续不会重复已完成章()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var service = Service(workspace, fake); var control = new RunControl();
        var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(), control, new InlineProgress(r => { if (r.State == ContinuousRunState.Committing) control.RequestPause(); }), default);
        Assert.Equal(ContinuousRunState.Paused, run.State); Assert.Equal(1, run.NextChapter); Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(run.Id, (await Service(workspace, new ScriptedTextModel()).LoadAsync(book.Id))!.Id);
        run = await service.ResumeAsync(session, run.Id, 20, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, run.State); Assert.Equal(6, fake.Requests.Count); Assert.Equal(3, session.Current.Revisions.History.Length);
    }
    [Fact]
    public async Task 阻塞问题停止当前章且不能跳到下一章()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Body), _ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(ChapterGenerationTests.Review with { FactsChecked = false })));
        var run = await Service(workspace, fake).StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default);
        Assert.Equal(ContinuousRunState.NeedsAttention, run.State); Assert.Equal(0, run.NextChapter); Assert.NotNull(run.PendingWorkId); Assert.Empty(session.Current.Revisions.History); Assert.Equal(2, fake.Requests.Count);
    }
    [Fact]
    public async Task 请求预算耗尽会停止且明确提高预算后可继续()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var service = Service(workspace, fake);
        var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(2), new(), null, default);
        Assert.Equal(ContinuousRunState.NeedsAttention, run.State); Assert.Equal(1, run.NextChapter); Assert.Equal(2, fake.Requests.Count);
        run = await service.ResumeAsync(session, run.Id, 6, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, run.State); Assert.Equal(6, fake.Requests.Count);
    }
    [Fact]
    public async Task 未知用量保守保留且未明确接受风险时不发出下一请求()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var steps = new List<Func<TextModelRequest, TextModelResponse>> { _ => new(ChapterGenerationTests.Body, ModelCompletion.Complete, new(null, null)) };
        steps.AddRange(Enumerable.Range(0, 6).Select<int, Func<TextModelRequest, TextModelResponse>>(i => _ => ChapterGenerationTests.Response(i % 2 == 0 ? ChapterGenerationTests.Body : ChapterGenerationTests.Json(ChapterGenerationTests.Review))));
        var fake = new ScriptedTextModel(steps.ToArray()); var service = Service(workspace, fake);
        var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default); Assert.Equal(ContinuousRunState.NeedsAttention, run.State); Assert.Single(fake.Requests);
        run = await service.ResumeAsync(session, run.Id, 20, 1000000, false, new(), null, default); Assert.Single(fake.Requests);
        var original = Assert.Single(service.Usage(run)); Assert.True(original.ChargedTokens > 0); Assert.False(original.RetryAcknowledged);
        run = await service.ResumeAsync(session, run.Id, 20, 1000000, true, new(), null, default); Assert.Equal(ContinuousRunState.Completed, run.State);
        var retained = service.Usage(run).Single(r => r.Id == original.Id); Assert.True(retained.RetryAcknowledged); Assert.Null(retained.Usage.InputTokens); Assert.Equal(original.ChargedTokens, retained.ChargedTokens);
    }
    [Fact]
    public async Task 作品提交后检查点写入中断可核对恢复且不重复模型请求()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var backing = new ContinuousRunStore(workspace.Paths); var faulty = new FailCheckpointStore(backing);
        await Assert.ThrowsAnyAsync<Exception>(() => Service(workspace, fake, faulty).StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default));
        Assert.Single(session.Current.Revisions.History); var checkpoint = backing.Latest(book.Id)!; Assert.Equal(ContinuousRunState.Committing, checkpoint.State); Assert.Equal(0, checkpoint.NextChapter);
        var restored = await Service(workspace, fake).ResumeAsync(session, checkpoint.Id, 20, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, restored.State); Assert.Equal(6, fake.Requests.Count); Assert.Equal(3, session.Current.Revisions.History.Length);
    }
    [Fact]
    public async Task 检查点后的作者修改不被恢复覆盖或自动发送()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var service = Service(workspace, fake); var control = new RunControl(); control.RequestPause();
        var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(), control, null, default); Assert.Equal(ContinuousRunState.Paused, run.State);
        session.Update(session.Current with { Title = "作者更改书名" });
        run = await service.ResumeAsync(session, run.Id, 20, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.NeedsAttention, run.State); Assert.Empty(fake.Requests); Assert.Equal("作者更改书名", session.Current.Title);
    }
    [Fact]
    public async Task 同书未结束运行不能新建且旧序列不能覆盖()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var service = Service(workspace, new ScriptedTextModel()); var control = new RunControl(); control.RequestPause();
        var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(), control, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default));
        var store = new ContinuousRunStore(workspace.Paths); Assert.Throws<InvalidOperationException>(() => store.Save(run));
        using (var lease = store.Acquire(book.Id)) Assert.Throws<InvalidOperationException>(() => new ContinuousRunStore(workspace.Paths).Acquire(book.Id));
        service.Abandon(book.Id, run.Id);
        var newer = await service.StartAsync(session, book.Chapters[0].Id, Policy(), control, null, default); Assert.NotEqual(run.Id, newer.Id);
    }
    [Fact]
    public async Task 自动补章纲也计入同一预算后继续三章()
    {
        await using var workspace = new TestWorkspace(); var configured = await ChapterGenerationTests.BookAsync(workspace);
        var book = BookProject.Create("简短创意", "邮差收到死亡预告信") with { Connection = configured.Connection }; await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var steps = new List<Func<TextModelRequest, TextModelResponse>> { _ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(PlanningTests.Proposal())) };
        steps.AddRange(Enumerable.Range(0, 6).Select<int, Func<TextModelRequest, TextModelResponse>>(i => _ => ChapterGenerationTests.Response(i % 2 == 0 ? ChapterGenerationTests.Body : ChapterGenerationTests.Json(ChapterGenerationTests.Review))));
        var fake = new ScriptedTextModel(steps.ToArray()); var service = Service(workspace, fake); var run = await service.StartAsync(session, book.Chapters[0].Id, Policy(plan: true), new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, run.State); Assert.Equal(7, service.Usage(run).Count); Assert.Single(session.Current.Planning.History); Assert.Equal(3, session.Current.Revisions.History.Length);
    }
    private sealed class InlineProgress(Action<ContinuousRun> action) : IProgress<ContinuousRun> { public void Report(ContinuousRun run) => action(run); }
    private sealed class FailCheckpointStore(IContinuousRunStore inner) : IContinuousRunStore
    {
        public IDisposable Acquire(Guid bookId) => inner.Acquire(bookId);
        public void Create(ContinuousRun run) => inner.Create(run);
        public ContinuousRun? Get(Guid id) => inner.Get(id);
        public ContinuousRun? Latest(Guid bookId) => inner.Latest(bookId);
        public void Save(ContinuousRun run) { if (run.NextChapter == 1) throw new IOException("模拟提交后运行盘中断"); inner.Save(run); }
    }
    [Fact]
    public async Task 读取中断记录只标状态不自动调用模型()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); var store = new ContinuousRunStore(workspace.Paths);
        var policy = Policy(); var run = new ContinuousRun(Guid.NewGuid(), book.Id, book.Chapters[0].Id, Guid.NewGuid(), policy, new(Guid.NewGuid(), 20, 1000000), book, [], 0, null, [], ContinuousRunState.Running, "模拟进程中断", 1); store.Create(run);
        var fake = new ScriptedTextModel(); var loaded = await Service(workspace, fake).LoadAsync(book.Id); Assert.Equal(ContinuousRunState.Interrupted, loaded!.State); Assert.Empty(fake.Requests);
        Assert.Equal(loaded.Sequence, (await Service(workspace, fake).LoadAsync(book.Id))!.Sequence);
    }
    [Fact]
    public async Task 取消在途请求保留候选与停止原因且不推进章节()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var model = new WaitingModel(); using var cancellation = new CancellationTokenSource(); var task = Service(workspace, model).StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, cancellation.Token);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel(); var run = await task;
        Assert.Equal(ContinuousRunState.Cancelled, run.State); Assert.Equal(0, run.NextChapter); Assert.Empty(session.Current.Revisions.History);
        Assert.NotNull(run.PendingWorkId); Assert.Equal(ChapterWorkState.Cancelled, new ChapterWorkStore(workspace.Paths).Get(run.PendingWorkId!.Value)!.State);
    }
    [Fact]
    public async Task 同连接请求串行且排队取消不预留预算()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace);
        var frozen = await workspace.Connections.FreezeAsync(book, ModelTask.Drafting); var store = new ModelRequestStore(workspace.Paths); var model = new WaitingModel();
        var service1 = new ModelRequestService(model, store); var service2 = new ModelRequestService(model, store);
        using var firstCancel = new CancellationTokenSource(); using var queuedCancel = new CancellationTokenSource();
        var first = service1.GenerateAsync(new(Guid.NewGuid(), frozen, "生成", "正文", false), new(Guid.NewGuid(), 1, 100000), null, firstCancel.Token);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); var queuedBudget = new RequestBudget(Guid.NewGuid(), 1, 100000);
        var second = service2.GenerateAsync(new(Guid.NewGuid(), frozen, "生成", "正文", false), queuedBudget, null, queuedCancel.Token);
        queuedCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); Assert.Empty(store.List(queuedBudget.Id)); Assert.Equal(1, model.Calls);
        firstCancel.Cancel(); await Assert.ThrowsAsync<ModelRequestException>(() => first);
    }
    [Fact]
    public async Task 提交检查点之外的修改不能被中断恢复掩盖()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var backing = new ContinuousRunStore(workspace.Paths);
        await Assert.ThrowsAnyAsync<Exception>(() => Service(workspace, fake, new FailCheckpointStore(backing)).StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default));
        session.Update(session.Current with { Title = "提交后又修改书名" }); var checkpoint = backing.Latest(book.Id)!;
        var result = await Service(workspace, fake).ResumeAsync(session, checkpoint.Id, 20, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.NeedsAttention, result.State); Assert.Equal(2, fake.Requests.Count); Assert.Single(session.Current.Revisions.History);
    }
    private sealed class WaitingModel : ITextModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Calls;
        public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); progress?.Report("已生成的部分正文"); Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }

    [Fact]
    public async Task 自动规划落库后中断不重复规划并继续原章身份()
    {
        await using var workspace = new TestWorkspace(); var configured = await ChapterGenerationTests.BookAsync(workspace);
        var book = BookProject.Create("规划中断", "邮差追查来信") with { Connection = configured.Connection }; await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var steps = new List<Func<TextModelRequest, TextModelResponse>> { _ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(PlanningTests.Proposal())) };
        steps.AddRange(Enumerable.Range(0, 6).Select<int, Func<TextModelRequest, TextModelResponse>>(i => _ => ChapterGenerationTests.Response(i % 2 == 0 ? ChapterGenerationTests.Body : ChapterGenerationTests.Json(ChapterGenerationTests.Review))));
        var fake = new ScriptedTextModel(steps.ToArray()); var backing = new ContinuousRunStore(workspace.Paths);
        var faulty = new ControlledRunStore(backing, r => { if (r.PreparedPlanning is null && r.Checkpoint.Planning.History.Length > 0) throw new IOException("规划落库后中断"); });
        await Assert.ThrowsAnyAsync<Exception>(() => Service(workspace, fake, faulty).StartAsync(session, book.Chapters[0].Id, Policy(plan: true), new(), null, default));
        var ids = session.Current.Chapters.Select(c => c.Id).ToArray(); Assert.Single(session.Current.Planning.History); Assert.Single(fake.Requests);
        var checkpoint = backing.Latest(book.Id)!; Assert.NotNull(checkpoint.PreparedPlanning);
        var restored = await Service(workspace, fake).ResumeAsync(session, checkpoint.Id, 20, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, restored.State); Assert.Equal(7, fake.Requests.Count); Assert.Equal(ids, session.Current.Chapters.Select(c => c.Id).ToArray()); Assert.Single(session.Current.Planning.History);
    }
    [Fact]
    public async Task 检查点瞬时写入失败不消耗序号并保存候选供无请求提交()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var fake = Success(3); var backing = new ContinuousRunStore(workspace.Paths); var failed = false;
        var faulty = new ControlledRunStore(backing, r => { if (!failed && r.State == ContinuousRunState.Committing) { failed = true; throw new IOException("仅失败一次"); } });
        var run = await Service(workspace, fake, faulty).StartAsync(session, book.Chapters[0].Id, Policy(), new(), null, default);
        Assert.Equal(ContinuousRunState.NeedsAttention, run.State); Assert.NotNull(backing.Get(run.Id)!.PendingWorkId); Assert.Empty(session.Current.Revisions.History);
        run = await Service(workspace, fake).ResumeAsync(session, run.Id, 20, 1000000, false, new(), null, default);
        Assert.Equal(ContinuousRunState.Completed, run.State); Assert.Equal(6, fake.Requests.Count);
    }
    [Fact]
    public async Task 重新激活较早运行后读取入口优先显示活动运行()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var service = Service(workspace, new ScriptedTextModel()); var pause = new RunControl(); pause.RequestPause();
        var a = await service.StartAsync(session, book.Chapters[0].Id, Policy(), pause, null, default); service.Abandon(book.Id, a.Id);
        var b = await service.StartAsync(session, book.Chapters[0].Id, Policy(), pause, null, default); service.Abandon(book.Id, b.Id);
        var resumed = await service.ResumeAsync(session, a.Id, 20, 1000000, false, pause, null, default);
        Assert.Equal(ContinuousRunState.Paused, resumed.State); Assert.Equal(a.Id, (await service.LoadAsync(book.Id))!.Id);
    }
    private sealed class ControlledRunStore(IContinuousRunStore inner, Action<ContinuousRun> beforeSave) : IContinuousRunStore
    {
        public IDisposable Acquire(Guid bookId) => inner.Acquire(bookId); public void Create(ContinuousRun run) => inner.Create(run);
        public ContinuousRun? Get(Guid id) => inner.Get(id); public ContinuousRun? Latest(Guid bookId) => inner.Latest(bookId);
        public void Save(ContinuousRun run) { beforeSave(run); inner.Save(run); }
    }

    [Fact]
    public async Task 另一运行活动时不能恢复旧运行修改作品或预算()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var service = Service(workspace, new ScriptedTextModel()); var pause = new RunControl(); pause.RequestPause();
        var a = await service.StartAsync(session, book.Chapters[0].Id, Policy(), pause, null, default); service.Abandon(book.Id, a.Id);
        var b = await service.StartAsync(session, book.Chapters[0].Id, Policy(), pause, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeAsync(session, a.Id, 30, 2000000, true, pause, null, default));
        Assert.Equal(b.Id, (await service.LoadAsync(book.Id))!.Id); Assert.Equal(20, new ContinuousRunStore(workspace.Paths).Get(a.Id)!.Budget.MaximumRequests);
    }

}
