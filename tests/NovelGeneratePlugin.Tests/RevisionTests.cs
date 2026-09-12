using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Application.Projects;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class RevisionTests
{
    [Fact]
    public async Task 提交期间的新编辑不会被旧快照覆盖而修订正文保持不可变()
    {
        await using var workspace = new TestWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PausingStore(workspace.Store, entered, release);
        await using var sessions = new ProjectSessions(store, workspace.Catalog, workspace.Recovery, new FileProjectLeaseProvider());
        await using var session = await sessions.CreateAsync(workspace.ProjectPath(), Story());
        var submission = Submission(session.Current, 0);
        var committing = session.CommitRevisionChangeAsync(book => RevisionRules.CommitWorking(book, submission));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => session.Update(session.Current with { Chapters = session.Current.Chapters.RemoveAt(0) }));
            session.Update(session.Current with { Chapters = session.Current.Chapters.SetItem(0, session.Current.Chapters[0] with { Text = "提交时继续输入的正文" }) });
        }
        finally { release.Set(); }
        await committing;
        Assert.Equal("提交时继续输入的正文", session.Current.Chapters[0].Text);
        Assert.Equal(submission.Text, session.Current.Revisions.History[0].Text);
        Assert.True((await session.SaveAsync()).Saved);
        var stored = workspace.Store.Read(session.Path);
        Assert.Equal("提交时继续输入的正文", stored.Project.Chapters[0].Text);
        Assert.Equal(submission.Text, stored.Project.Revisions.History[0].Text);
    }
    [Fact]
    public void 跨书候选与正式章节重排不能绕过来源校验()
    {
        var book = Story(); var candidate = Submission(book, 0, facts: [new FactDelta("状态", null, "存活")]);
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(book with { Id = Guid.NewGuid() }, candidate));
        book = book with { Revisions = RevisionRules.CommitWorking(book, candidate) };
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value, true) };
        book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 1, facts: [new FactDelta("状态", "存活", "死亡")])) };
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, book.Chapters[1].Id, book.Revisions.Head(book.Chapters[1].Id).WorkingId!.Value, true) };
        Assert.Throws<InvalidDataException>(() => (book with { Chapters = book.Chapters.Reverse().ToImmutableArray() }).Validate());
    }
    [Fact]
    public async Task 外部写事务改变格式后旧写入不能覆盖未知版本()
    {
        await using var workspace = new TestWorkspace(); var book = Story();
        workspace.Store.Create(workspace.ProjectPath(), book);
        using var external = ProjectStore.Connect(workspace.ProjectPath());
        using var transaction = external.BeginTransaction();
        using var command = external.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Task.Run(() => { started.TrySetResult(); return workspace.Store.Save(workspace.ProjectPath(), book with { Title = "不能写入" }, 0); });
        await started.Task; await Task.Delay(100);
        transaction.Commit();
        await Assert.ThrowsAsync<NotSupportedException>(() => write);
        using var check = external.CreateCommand(); check.CommandText = "SELECT version FROM project";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "PRAGMA user_version"; Assert.Equal(99L, check.ExecuteScalar());
    }
    private sealed class PausingStore(IProjectStore inner, TaskCompletionSource entered, ManualResetEventSlim release) : IProjectStore
    {
        private int _writes;
        public StoredProject Create(string path, BookProject project) => inner.Create(path, project);
        public StoredProject Read(string path) => inner.Read(path);
        public long Save(string path, BookProject project, long expectedVersion)
        {
            if (Interlocked.Increment(ref _writes) == 1) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
            return inner.Save(path, project, expectedVersion);
        }
    }
    private static BookProject Story()
    {
        var book = BookProject.Create("雾港来信");
        book = BookEdits.AddChapter(book, book.Chapters[0].Id).Project;
        return book with { Chapters = book.Chapters.Select((c, i) => c with { Text = $"第 {i + 1} 章：阿宁在雾港收到一封未来的信。" }).ToImmutableArray() };
    }
    private static DraftSubmission Submission(BookProject book, int index, Guid? run = null, ImmutableArray<FactDelta> facts = default)
    {
        var chapter = book.Chapters[index]; var head = book.Revisions.Head(chapter.Id);
        return new DraftSubmission(book.Id, chapter.Id, head.WorkingId, head.FormalId, RevisionRules.Hash(chapter.Text),
            RevisionRules.ContextStamp(book, chapter.Id), chapter.Text, "本章摘要", facts.IsDefault ? [] : facts,
            RevisionCheck.NotChecked, run ?? book.Revisions.ActiveRunId ?? Guid.NewGuid(), Guid.NewGuid());
    }
    [Fact]
    public void 工作稿事实在人工定稿之前不进入正式记忆()
    {
        var book = Story(); var submission = Submission(book, 0, facts: [new FactDelta("阿宁.位置", null, "雾港")]);
        book = book with { Revisions = RevisionRules.CommitWorking(book, submission) };
        Assert.Empty(RevisionRules.MemoryBefore(book, book.Chapters[1].Id, false));
        Assert.Equal("雾港", RevisionRules.MemoryBefore(book, book.Chapters[1].Id, true)["阿宁.位置"]);
        var working = book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value;
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, working, true) };
        Assert.Equal("雾港", RevisionRules.MemoryBefore(book, book.Chapters[1].Id, false)["阿宁.位置"]);
        Assert.Equal(RevisionCheck.NotChecked, book.Revisions.Get(working)!.Check);
    }
    [Fact]
    public void 放弃工作稿不会改变正式正文和记忆()
    {
        var book = Story(); book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 0)) };
        var first = book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value;
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, first, true) };
        book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 1)) };
        var discarded = RevisionRules.DiscardWorking(book);
        Assert.Equal(first, discarded.Head(book.Chapters[0].Id).FormalId);
        Assert.Null(discarded.Head(book.Chapters[1].Id).WorkingId); Assert.Equal(2, discarded.History.Length);
        Assert.Null(discarded.ActiveRunId);
    }
    [Fact]
    public void 正式稿只能连续定稿而回退同时撤销后续正文及记忆来源()
    {
        var book = Story(); book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 0, facts: [new FactDelta("信件.持有者", null, "阿宁")])) };
        book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 1, facts: [new FactDelta("信件.持有者", "阿宁", "老船长")])) };
        var one = book.Chapters[0].Id; var two = book.Chapters[1].Id;
        Assert.Throws<InvalidOperationException>(() => RevisionRules.FinalizeChapter(book, two, book.Revisions.Head(two).WorkingId!.Value, true));
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, one, book.Revisions.Head(one).WorkingId!.Value, true) };
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, two, book.Revisions.Head(two).WorkingId!.Value, true) };
        book = book with { Revisions = RevisionRules.RollbackFrom(book, one) };
        Assert.Null(book.Revisions.Head(one).FormalId); Assert.Null(book.Revisions.Head(two).FormalId);
        Assert.Empty(RevisionRules.MemoryBefore(book, two, false)); Assert.Equal(2, book.Revisions.History.Length);
    }
    [Fact]
    public void 正文摘要修订身份与前文上下文任一过期都不能提交()
    {
        var book = Story(); var candidate = Submission(book, 0);
        var changed = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "作者新改的正文" }) };
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(changed, candidate));
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(book, candidate with { ExpectedWorkingId = Guid.NewGuid() }));
        var second = Submission(book, 1);
        book = book with { Revisions = RevisionRules.CommitWorking(book, candidate) };
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(book, second));
    }
    [Fact]
    public void 同一操作可重复返回但不能偷换内容或越过活动运行()
    {
        var book = Story(); var candidate = Submission(book, 0);
        book = book with { Revisions = RevisionRules.CommitWorking(book, candidate) };
        Assert.Same(book.Revisions, RevisionRules.CommitWorking(book, candidate));
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(book, candidate with { Summary = "不同摘要" }));
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(book, Submission(book, 1, Guid.NewGuid())));
    }
    [Fact]
    public void 不完整正文事实旧值不符和检查失败有明确拒绝路径()
    {
        var book = Story(); var empty = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "" }) };
        Assert.Throws<InvalidDataException>(() => RevisionRules.CommitWorking(empty, Submission(empty, 0)));
        Assert.Throws<InvalidOperationException>(() => RevisionRules.CommitWorking(book, Submission(book, 0, facts: [new FactDelta("地点", "不存在的旧值", "港口")])));
        book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 0) with { Check = RevisionCheck.Failed }) };
        Assert.Throws<InvalidOperationException>(() => RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value, true));
    }
    [Fact]
    public void 编辑保存不改变已提交的正文且编辑后不能误定稿旧正文()
    {
        var book = Story(); book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 0)) };
        var id = book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value; var text = book.Revisions.Get(id)!.Text;
        book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "后来编辑的文本" }) };
        Assert.Equal(text, book.Revisions.Get(id)!.Text);
        Assert.Throws<InvalidOperationException>(() => RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, id, true));
    }
    [Fact]
    public void 历史不能删除或原地改写而独立复制保留历史并重分配身份()
    {
        var book = Story(); book = book with { Revisions = RevisionRules.CommitWorking(book, Submission(book, 0)) };
        Assert.Throws<InvalidOperationException>(() => RevisionLedger.Empty.EnsureAppendOnlyFrom(book.Revisions));
        var revision = book.Revisions.History[0];
        Assert.Throws<InvalidOperationException>(() => (book.Revisions with { History = [revision with { Summary = "原地覆盖" }] }).EnsureAppendOnlyFrom(book.Revisions));
        var copy = book.CopyAsNew(); copy.Validate(); Assert.Single(copy.Revisions.History);
        Assert.NotEqual(book.Revisions.History[0].Id, copy.Revisions.History[0].Id);
        Assert.NotEqual(book.Revisions.ActiveRunId, copy.Revisions.ActiveRunId);
        Assert.Equal(revision.Text, copy.Revisions.History[0].Text);
        Assert.Equal(RevisionRules.ContextStamp(copy, copy.Chapters[0].Id), copy.Revisions.History[0].ContextStamp);
    }
    [Fact]
    public async Task 修订和事实同事务持久化并拒绝直接覆盖历史()
    {
        await using var workspace = new TestWorkspace();
        var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), Story());
        var submission = Submission(session.Current, 0, facts: [new FactDelta("角色.身份", null, "邮差")]);
        await session.CommitRevisionChangeAsync(book => RevisionRules.CommitWorking(book, submission));
        var expected = session.Current.Revisions.History[0]; await session.DisposeAsync();
        var stored = workspace.Store.Read(workspace.ProjectPath()); var actual = stored.Project.Revisions.History[0];
        Assert.Equal(expected.Text, actual.Text); Assert.Equal(expected.Facts.ToArray(), actual.Facts.ToArray());
        Assert.Throws<InvalidOperationException>(() => workspace.Store.Save(workspace.ProjectPath(), stored.Project with { Revisions = RevisionLedger.Empty }, stored.Version));
    }
    [Fact]
    public async Task 数据库提交中断不会把半个修订留在会话或重开项目()
    {
        await using var workspace = new TestWorkspace();
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), Story());
        using (var connection = ProjectStore.Connect(session.Path))
        { using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_update BEFORE UPDATE ON project BEGIN SELECT RAISE(ABORT, '测试事务中断'); END;"; command.ExecuteNonQuery(); }
        var submission = Submission(session.Current, 0, facts: [new FactDelta("位置", null, "雾港")]);
        await Assert.ThrowsAsync<SqliteException>(() => session.CommitRevisionChangeAsync(book => RevisionRules.CommitWorking(book, submission)));
        Assert.Empty(session.Current.Revisions.History); Assert.Empty(workspace.Store.Read(session.Path).Project.Revisions.History);
    }
    [Fact]
    public async Task 旧版项目先完整备份再迁移且不把既有编辑稿变成正式稿()
    {
        await using var workspace = new TestWorkspace(); var project = Story();
        workspace.Store.Create(workspace.ProjectPath(), project);
        using (var connection = ProjectStore.Connect(workspace.ProjectPath()))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE project SET snapshot=$snapshot; PRAGMA user_version=1;";
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(new { project.Id, project.Title, project.Idea, project.Volumes, project.Chapters }));
            command.ExecuteNonQuery();
        }
        var migrated = workspace.Store.Read(workspace.ProjectPath());
        Assert.Equal(project.Id, migrated.Project.Id); Assert.Equal(project.Chapters[0].Text, migrated.Project.Chapters[0].Text);
        Assert.Empty(migrated.Project.Revisions.History);
        var backup = Assert.Single(Directory.GetFiles(workspace.Root, "*.before-v2-*.noveldb"));
        using var source = ProjectStore.Connect(backup, SqliteOpenMode.ReadOnly);
        using var check = source.CreateCommand(); check.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT snapshot FROM project"; Assert.Contains("雾港", JsonSerializer.Deserialize<JsonElement>((string)check.ExecuteScalar()!).GetProperty("Title").GetString());
    }
}
