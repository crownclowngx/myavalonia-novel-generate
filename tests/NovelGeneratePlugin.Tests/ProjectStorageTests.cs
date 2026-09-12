using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class ProjectStorageTests
{
    [Fact]
    public async Task 中文卷章和空标题编辑缓冲在关闭重开后保持一致()
    {
        await using var workspace = new TestWorkspace();
        var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("雾港来信", "邮差发现未来的信"));
        var original = session.Current;
        var edited = original with { Title = "", Chapters = original.Chapters.SetItem(0, original.Chapters[0] with { Title = "", Outline = "午夜送信", Text = "雾从码头涌来。\n阿宁拆开信封：明天别来。🚢" }) };
        session.Update(edited);
        await session.DisposeAsync();
        await using var reopened = await workspace.Sessions.OpenAsync(workspace.ProjectPath());
        Assert.Equal(edited.Id, reopened.Id);
        Assert.Equal(edited.Title, reopened.Current.Title);
        Assert.Equal(edited.Idea, reopened.Current.Idea);
        Assert.Equal(edited.Volumes.ToArray(), reopened.Current.Volumes.ToArray());
        Assert.Equal(edited.Chapters.ToArray(), reopened.Current.Chapters.ToArray());
        Assert.Equal(SaveState.Saved, reopened.Status.State);
    }
    [Fact]
    public async Task 双书并发保存不串写且最后一次编辑获保存()
    {
        await using var workspace = new TestWorkspace();
        await using var a = await workspace.Sessions.CreateAsync(workspace.ProjectPath("甲"), BookProject.Create("甲书"));
        await using var b = await workspace.Sessions.CreateAsync(workspace.ProjectPath("乙"), BookProject.Create("乙书"));
        for (var i = 0; i < 50; i++) { a.Update(a.Current with { Idea = "甲" + i }); b.Update(b.Current with { Idea = "乙" + i }); }
        await Task.WhenAll(a.SaveAsync(), b.SaveAsync(), a.SaveAsync());
        Assert.Equal("甲49", workspace.Store.Read(a.Path).Project.Idea);
        Assert.Equal("乙49", workspace.Store.Read(b.Path).Project.Idea);
    }
    [Fact]
    public async Task 自动保存可独立于显式保存和关闭完成()
    {
        await using var workspace = new TestWorkspace();
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("自动保存"));
        session.Update(session.Current with { Idea = "自动保存后的新创意" });
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (session.Status.State != SaveState.Saved && DateTime.UtcNow < deadline) await Task.Delay(30);
        Assert.Equal(SaveState.Saved, session.Status.State);
        Assert.Equal("自动保存后的新创意", workspace.Store.Read(session.Path).Project.Idea);
        Assert.DoesNotContain("恢复副本", session.Status.Message);
    }
    [Fact]
    public async Task 规范路径和复制文件的同书身份均拒绝重复写会话()
    {
        await using var workspace = new TestWorkspace();
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("身份隔离"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Sessions.OpenAsync(Path.Combine(workspace.Root, ".", "作品.noveldb")));
        File.Copy(session.Path, workspace.ProjectPath("副本"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Sessions.OpenAsync(workspace.ProjectPath("副本")));
        await using var otherManager = new ProjectSessions(workspace.Store, workspace.Catalog, workspace.Recovery, new FileProjectLeaseProvider());
        await Assert.ThrowsAsync<IOException>(() => otherManager.OpenAsync(session.Path));
    }
    [Fact]
    public async Task 新建不覆盖现有文件且失败项目不登记目录()
    {
        await using var workspace = new TestWorkspace();
        File.WriteAllText(workspace.ProjectPath(), "用户原文件");
        await Assert.ThrowsAsync<IOException>(() => workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("不能覆盖")));
        Assert.Equal("用户原文件", File.ReadAllText(workspace.ProjectPath()));
        Assert.Empty(workspace.Catalog.List());
    }
    [Fact]
    public async Task 取消新建没有项目也没有目录记录()
    {
        await using var workspace = new TestWorkspace();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("已取消"), new CancellationToken(true)));
        Assert.False(File.Exists(workspace.ProjectPath())); Assert.Empty(workspace.Catalog.List());
    }
    [Fact]
    public async Task 未知格式拒绝读取而不盲目迁移()
    {
        await using var workspace = new TestWorkspace();
        workspace.Store.Create(workspace.ProjectPath(), BookProject.Create("未来版本"));
        using (var connection = ProjectStore.Connect(workspace.ProjectPath()))
        { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        var bytes = await File.ReadAllBytesAsync(workspace.ProjectPath());
        await Assert.ThrowsAsync<NotSupportedException>(() => workspace.Sessions.OpenAsync(workspace.ProjectPath()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.ProjectPath()));
    }
    [Fact]
    public async Task 旧快照不能覆盖新版本且失败内容可从恢复副本读取()
    {
        await using var workspace = new TestWorkspace();
        var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("版本检查"));
        workspace.Store.Save(session.Path, session.Current with { Idea = "外部最新版本" }, 0);
        session.Update(session.Current with { Idea = "本地尚未落盘的输入" });
        var result = await session.SaveAsync();
        Assert.False(result.Saved); Assert.True(result.RecoveryAvailable); Assert.Equal(SaveState.Failed, session.Status.State);
        Assert.Equal("外部最新版本", workspace.Store.Read(session.Path).Project.Idea);
        await session.DisposeAsync();
        var entry = Assert.Single(workspace.Recovery.List());
        Assert.Equal("本地尚未落盘的输入", workspace.Recovery.Read(entry.Path).Project.Idea);
    }
    [Fact]
    public async Task 数据库真实写锁失败后可重试并清除自己的恢复副本()
    {
        await using var workspace = new TestWorkspace();
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("写锁测试"));
        using (var connection = ProjectStore.Connect(session.Path))
        using (var transaction = connection.BeginTransaction())
        {
            session.Update(session.Current with { Idea = "写锁期间输入" });
            var result = await session.SaveAsync();
            Assert.False(result.Saved); Assert.True(result.RecoveryAvailable);
            Assert.NotEmpty(workspace.Recovery.List());
            transaction.Rollback();
        }
        Assert.True((await session.SaveAsync()).Saved);
        Assert.Equal("写锁期间输入", workspace.Store.Read(session.Path).Project.Idea);
        Assert.Empty(workspace.Recovery.List());
    }
    [Fact]
    public async Task 只读项目失败时保存恢复副本()
    {
        await using var workspace = new TestWorkspace();
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("只读"));
        File.SetAttributes(session.Path, FileAttributes.ReadOnly);
        try
        {
            session.Update(session.Current with { Idea = "不能丢失的编辑" });
            var result = await session.SaveAsync(); Assert.False(result.Saved); Assert.True(result.RecoveryAvailable);
        }
        finally { File.SetAttributes(session.Path, FileAttributes.Normal); }
        Assert.True((await session.SaveAsync()).Saved);
    }
    [Fact]
    public async Task 双重保存失败不能关闭并可在故障解除后再次关闭()
    {
        await using var workspace = new TestWorkspace();
        var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("恢复路径故障"));
        Directory.CreateDirectory(workspace.Paths.Root);
        File.WriteAllText(workspace.Paths.RecoveryDirectory, "占用恢复目录名的文件");
        File.SetAttributes(session.Path, FileAttributes.ReadOnly);
        session.Update(session.Current with { Idea = "留在内存等待恢复" });
        try { await Assert.ThrowsAsync<IOException>(async () => await session.DisposeAsync()); }
        finally { File.SetAttributes(session.Path, FileAttributes.Normal); File.Delete(workspace.Paths.RecoveryDirectory); }
        await session.DisposeAsync();
        Assert.Equal("留在内存等待恢复", workspace.Store.Read(session.Path).Project.Idea);
    }
    [Fact]
    public async Task 目录不可写不影响已创建项目的继续编辑()
    {
        await using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.Paths.Root); Directory.CreateDirectory(workspace.Paths.Catalog);
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), BookProject.Create("目录故障"));
        Assert.NotNull(session.CatalogWarning);
        session.Update(session.Current with { Idea = "项目仍然可用" });
        Assert.True((await session.SaveAsync()).Saved);
        Assert.Equal("项目仍然可用", workspace.Store.Read(session.Path).Project.Idea);
    }
    [Fact]
    public async Task 恢复为新作品必须重分配书卷章身份()
    {
        await using var workspace = new TestWorkspace();
        var original = BookProject.Create("恢复原书"); var copy = original.CopyAsNew();
        copy.Validate(); Assert.NotEqual(original.Id, copy.Id); Assert.NotEqual(original.Volumes[0].Id, copy.Volumes[0].Id);
        Assert.NotEqual(original.Chapters[0].Id, copy.Chapters[0].Id); Assert.Equal(copy.Volumes[0].Id, copy.Chapters[0].VolumeId);
        await using var a = await workspace.Sessions.CreateAsync(workspace.ProjectPath("原"), original);
        await using var b = await workspace.Sessions.CreateAsync(workspace.ProjectPath("恢复"), copy);
        Assert.NotEqual(a.Id, b.Id);
    }
    [Fact]
    public void 无效身份与跨卷孤儿章节拒绝进入项目()
    {
        var project = BookProject.Create("验证");
        Assert.Throws<InvalidDataException>(() => (project with { Id = Guid.Empty }).Validate());
        Assert.Throws<InvalidDataException>(() => (project with { Chapters = project.Chapters.SetItem(0, project.Chapters[0] with { VolumeId = Guid.NewGuid() }) }).Validate());
        Assert.Throws<InvalidDataException>(() => (project with { Chapters = project.Chapters.Add(project.Chapters[0]) }).Validate());
    }
}
