using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

/// <summary>用受控的存储边界稳定复现竞争窗口；落盘和恢复仍使用真实临时文件。</summary>
public sealed class SaveRaceTests
{
    [Fact]
    public async Task 旧编辑版本的恢复成功不能宣称更新的输入已被保护()
    {
        await using var workspace = new TestWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new ControlledRecovery(workspace.Recovery)
        {
            BeforeWrite = snapshot => { if (snapshot.EditVersion == 1) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); } }
        };
        await using var sessions = new ProjectSessions(new FailingSaveStore(workspace.Store), workspace.Catalog, recovery, new FileProjectLeaseProvider());
        await using var session = await sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("竞争测试"));
        session.Update(session.Current with { Idea = "较早的输入" });
        var save = session.SaveAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.Update(session.Current with { Idea = "保存期间的新输入" });
        }
        finally { release.Set(); }
        var oldResult = await save;
        Assert.False(oldResult.RecoveryAvailable);
        Assert.True((await session.SaveAsync()).RecoveryAvailable);
        Assert.Equal("保存期间的新输入", workspace.Recovery.Read(session.RecoveryPath).Project.Idea);
    }
    [Fact]
    public async Task 有效恢复副本在重试失败后仍可用于关闭且最终释放不重复写入()
    {
        await using var workspace = new TestWorkspace();
        var recovery = new ControlledRecovery(workspace.Recovery);
        await using var sessions = new ProjectSessions(new FailingSaveStore(workspace.Store), workspace.Catalog, recovery, new FileProjectLeaseProvider());
        var session = await sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("已有恢复"));
        session.Update(session.Current with { Idea = "当前编辑已受保护" });
        Assert.True((await session.SaveAsync()).RecoveryAvailable);
        recovery.FailWrites = true;
        Assert.True((await session.SaveAsync()).RecoveryAvailable);
        Assert.True((await session.PrepareCloseAsync()).RecoveryAvailable);
        var attempts = recovery.WriteAttempts;
        await session.DisposeAsync();
        Assert.Equal(attempts, recovery.WriteAttempts);
        Assert.Equal("当前编辑已受保护", workspace.Recovery.Read(session.RecoveryPath).Project.Idea);
    }
    [Fact]
    public async Task 关闭准备失败之后仍可继续输入并重新保存()
    {
        await using var workspace = new TestWorkspace();
        var recovery = new ControlledRecovery(workspace.Recovery) { FailWrites = true };
        await using var sessions = new ProjectSessions(new FailingSaveStore(workspace.Store), workspace.Catalog, recovery, new FileProjectLeaseProvider());
        await using var session = await sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("继续编辑"));
        session.Update(session.Current with { Idea = "第一次输入" });
        Assert.False((await session.PrepareCloseAsync()).RecoveryAvailable);
        session.Update(session.Current with { Idea = "失败后继续输入" });
        recovery.FailWrites = false;
        Assert.True((await session.PrepareCloseAsync()).RecoveryAvailable);
        Assert.Equal("失败后继续输入", workspace.Recovery.Read(session.RecoveryPath).Project.Idea);
    }
    [Fact]
    public async Task 一个被占用的恢复文件不遮蔽其他正常副本()
    {
        await using var workspace = new TestWorkspace();
        var project = BookProject.Create("正常副本");
        var valid = workspace.Recovery.PathFor(project.Id, Guid.NewGuid());
        workspace.Recovery.Write(valid, new RecoverySnapshot(1, workspace.ProjectPath(), 0, project, DateTimeOffset.UtcNow));
        var locked = Path.Combine(workspace.Paths.RecoveryDirectory, "locked.json");
        using var file = new FileStream(locked, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var entries = workspace.Recovery.List();
        Assert.Equal(2, entries.Count); Assert.Contains(entries, e => e.Title == "正常副本");
        Assert.Contains(entries, e => e.Title == "无法读取的恢复副本");
    }
    private sealed class FailingSaveStore(IProjectStore inner) : IProjectStore
    {
        public StoredProject Create(string path, BookProject project) => inner.Create(path, project);
        public StoredProject Read(string path) => inner.Read(path);
        public long Save(string path, BookProject project, long expectedVersion) => throw new IOException("测试注入：作品存储暂时不可写");
    }
    private sealed class ControlledRecovery(IRecoveryStore inner) : IRecoveryStore
    {
        public bool FailWrites { get; set; }
        public int WriteAttempts { get; private set; }
        public Action<RecoverySnapshot>? BeforeWrite { get; init; }
        public string PathFor(Guid projectId, Guid sessionId) => inner.PathFor(projectId, sessionId);
        public void Write(string path, RecoverySnapshot snapshot)
        {
            WriteAttempts++; BeforeWrite?.Invoke(snapshot);
            if (FailWrites) throw new IOException("测试注入：恢复位置暂时不可写");
            inner.Write(path, snapshot);
        }
        public RecoverySnapshot Read(string path) => inner.Read(path);
        public IReadOnlyList<RecoveryEntry> List() => inner.List();
        public void Delete(string path) => inner.Delete(path);
    }
}
