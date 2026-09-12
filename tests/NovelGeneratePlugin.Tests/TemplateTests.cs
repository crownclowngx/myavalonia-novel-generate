using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class TemplateTests
{
    [Fact]
    public async Task 发布保存后的清理期间切换不能把版本写到另一模板()
    {
        await using var workspace = new TestWorkspace(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = await workspace.Templates.CreateAsync(Draft("甲")); var b = await workspace.Templates.CreateAsync(Draft("乙"));
        var store = new ControlledTemplateStore(new TemplateStore(workspace.Paths))
        { BeforeDeleteRecovery = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        await using var tool = new TemplateLibraryTool(new TemplateLibrary(store), workspace.Closing); await tool.InitializeAsync();
        tool.SelectedTemplate = tool.Templates.Single(t => t.Id == a.Id); tool.World = "甲的新版本";
        var publishing = tool.PublishVersionCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); tool.SelectedTemplate = tool.Templates.Single(t => t.Id == b.Id);
            Assert.Equal(a.Id, tool.SelectedTemplate!.Id);
        }
        finally { release.Set(); }
        await publishing;
        Assert.Equal("甲的新版本", Assert.Single(store.Read(a.Id).Versions).Content.World); Assert.Empty(store.Read(b.Id).Versions);
    }
    [Fact]
    public async Task 从模板创建两书产生新身份且无效采用不创建文件()
    {
        await using var workspace = new TestWorkspace();
        await workspace.Templates.CreatePublishedAsync(Draft());
        await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("模板新书"), CancellationToken.None);
        document.SelectedTemplateChoice = Assert.Single(document.TemplateChoices);
        workspace.Interaction.NextPath = workspace.ProjectPath("甲"); await document.NewFromTemplateCommand.ExecuteAsync(null);
        var a = workspace.Store.Read(workspace.ProjectPath("甲")).Project;
        workspace.Interaction.NextPath = workspace.ProjectPath("乙"); await document.NewFromTemplateCommand.ExecuteAsync(null);
        var b = workspace.Store.Read(workspace.ProjectPath("乙")).Project;
        Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.Chapters[0].Id, b.Chapters[0].Id); Assert.Equal(Draft().Content, b.Profile);
        document.UseWorld = document.UseStyle = document.UseMethods = document.UseRules = false;
        workspace.Interaction.NextPath = workspace.ProjectPath("不可创建"); await document.NewFromTemplateCommand.ExecuteAsync(null);
        Assert.False(File.Exists(workspace.ProjectPath("不可创建"))); Assert.Equal(workspace.ProjectPath("乙"), document.ProjectPath);
    }
    [Fact]
    public async Task 关闭期间的新修改不能被旧保存结果标记为安全()
    {
        await using var workspace = new TestWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledTemplateStore(new TemplateStore(workspace.Paths))
        {
            BeforeSave = _ => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        };
        await using var tool = new TemplateLibraryTool(new TemplateLibrary(store), workspace.Closing); await tool.InitializeAsync(); tool.World = "先保存的 A";
        var closing = tool.SaveBeforeCloseAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(closing, tool.SaveBeforeCloseAsync()); Assert.False(tool.CanManage);
            tool.World = "保存期间更新的 B";
        }
        finally { release.Set(); }
        Assert.False(await closing); Assert.True(tool.IsDirty);
        Assert.True(await tool.SaveBeforeCloseAsync());
        Assert.Equal("保存期间更新的 B", Assert.Single(store.List()).Draft.Content.World);
    }
    [Fact]
    public async Task 本书创建已发布模板失败没有残留重试只产生一个完整模板()
    {
        await using var workspace = new TestWorkspace();
        var store = new ControlledTemplateStore(new TemplateStore(workspace.Paths)) { BeforeSave = _ => throw new IOException("注入提交失败") };
        var library = new TemplateLibrary(store);
        await Assert.ThrowsAsync<IOException>(() => library.CreatePublishedAsync(Draft())); Assert.Empty(store.List());
        store.BeforeSave = asset => Assert.Single(asset.Versions);
        await library.CreatePublishedAsync(Draft());
        Assert.Single(Assert.Single(store.List()).Versions);
    }
    [Fact]
    public async Task 模板数据列与快照身份不一致时拒绝读取和保存()
    {
        await using var workspace = new TestWorkspace();
        var asset = await workspace.Templates.CreateAsync(Draft());
        using (var connection = ProjectStore.Connect(workspace.Paths.Catalog))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE templates SET revision=revision+1"; command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.Templates.ReadAsync(asset.Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.Templates.ListAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.Templates.SaveDraftAsync(asset, Draft("禁止覆盖")));
    }
    private static TemplateDraft Draft(string name = "雾港悬疑") => new(name, ["悬疑", "近景视角"],
        new WritingProfile("雾港与未来邮局", "克制，短句", "每章一个具体障碍", "不用总结式说教"), "手工测试规范");
    [Fact]
    public async Task 命名模板草案编辑与新版本不会覆盖既有版本()
    {
        await using var workspace = new TestWorkspace();
        var asset = await workspace.Templates.CreateAsync(Draft());
        asset = await workspace.Templates.PublishAsync(asset); var first = asset.Versions[0];
        asset = await workspace.Templates.SaveDraftAsync(asset, asset.Draft with { Name = "改名后的悬疑模板", Content = asset.Draft.Content with { Style = "更冷静的新文风" } });
        Assert.Equal(first, asset.Versions[0]);
        asset = await workspace.Templates.PublishAsync(asset);
        var loaded = await workspace.Templates.ReadAsync(asset.Id);
        Assert.Equal(asset.Id, loaded.Id); Assert.Equal(first, loaded.Versions[0]);
        Assert.Equal("更冷静的新文风", loaded.Versions[1].Content.Style); Assert.Equal(2, loaded.Versions[1].Number);
    }
    [Fact]
    public async Task 模板旧版本写入与历史原地替换均被拒绝()
    {
        await using var workspace = new TestWorkspace(); var store = new TemplateStore(workspace.Paths);
        var old = await workspace.Templates.CreateAsync(Draft()); var saved = await workspace.Templates.PublishAsync(old);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Templates.SaveDraftAsync(old, Draft("旧对象写入")));
        var modified = saved with { Versions = saved.Versions.SetItem(0, saved.Versions[0] with { Content = WritingProfile.Empty }) };
        Assert.Throws<InvalidOperationException>(() => store.Save(modified, saved.Revision));
        Assert.Equal(Draft().Content, store.Read(saved.Id).Versions[0].Content);
    }
    [Fact]
    public async Task 同模板的两书快照与库更新彼此隔离且重开保留()
    {
        await using var workspace = new TestWorkspace();
        var asset = await workspace.Templates.PublishAsync(await workspace.Templates.CreateAsync(Draft()));
        var choice = Assert.Single(await workspace.Templates.ChoicesAsync());
        var a = await workspace.Templates.AdoptAsync(BookProject.Create("甲书"), choice, ProfileDimensions.All);
        var b = await workspace.Templates.AdoptAsync(BookProject.Create("乙书"), choice, ProfileDimensions.All);
        a = a with { Profile = a.Profile with { World = "甲书自己的城市" } };
        asset = await workspace.Templates.SaveDraftAsync(asset, asset.Draft with { Content = WritingProfile.Empty });
        await workspace.Templates.PublishAsync(asset);
        Assert.Equal("雾港与未来邮局", b.Profile.World);
        Assert.Equal("雾港与未来邮局", a.AdoptedTemplate!.SourceContent.World);
        workspace.Store.Create(workspace.ProjectPath("甲"), a); workspace.Store.Create(workspace.ProjectPath("乙"), b);
        Assert.Equal("甲书自己的城市", workspace.Store.Read(workspace.ProjectPath("甲")).Project.Profile.World);
        Assert.Equal("雾港与未来邮局", workspace.Store.Read(workspace.ProjectPath("乙")).Project.Profile.World);
    }
    [Fact]
    public async Task 按维度采用保留未选择的作者规范()
    {
        await using var workspace = new TestWorkspace();
        var asset = await workspace.Templates.PublishAsync(await workspace.Templates.CreateAsync(Draft()));
        var book = BookProject.Create("局部采用") with { Profile = new WritingProfile("本书世界", "作者原有文风", "作者方法", "作者规则") };
        var adopted = TemplateAdoptionRules.Adopt(book, asset, asset.Versions[0].Id, ProfileDimensions.World | ProfileDimensions.Methods);
        Assert.Equal(asset.Draft.Content.World, adopted.Profile.World); Assert.Equal("作者原有文风", adopted.Profile.Style);
        Assert.Equal("作者规则", adopted.Profile.Rules);
        Assert.Throws<InvalidOperationException>(() => TemplateAdoptionRules.Adopt(book, asset, asset.Versions[0].Id, ProfileDimensions.None));
    }
    [Fact]
    public async Task 模板归档或源删除不会使已采用作品失效()
    {
        await using var workspace = new TestWorkspace();
        var asset = await workspace.Templates.PublishAsync(await workspace.Templates.CreateAsync(Draft()));
        var choice = Assert.Single(await workspace.Templates.ChoicesAsync());
        var book = await workspace.Templates.AdoptAsync(BookProject.Create("独立快照"), choice, ProfileDimensions.All);
        workspace.Store.Create(workspace.ProjectPath(), book);
        asset = await workspace.Templates.ArchiveAsync(asset, true); Assert.Empty(await workspace.Templates.ChoicesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Templates.AdoptAsync(BookProject.Create("新书"), choice, ProfileDimensions.All));
        using (var connection = ProjectStore.Connect(workspace.Paths.Catalog))
        { using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM templates"; command.ExecuteNonQuery(); }
        var loaded = workspace.Store.Read(workspace.ProjectPath()).Project;
        Assert.Equal(Draft().Content, loaded.Profile); Assert.NotNull(loaded.AdoptedTemplate);
    }
    [Fact]
    public async Task 复制模板产生独立身份与可编辑草案()
    {
        await using var workspace = new TestWorkspace();
        var source = await workspace.Templates.PublishAsync(await workspace.Templates.CreateAsync(Draft()));
        var copy = await workspace.Templates.CopyAsync(source);
        Assert.NotEqual(source.Id, copy.Id); Assert.Empty(copy.Versions); Assert.Equal(source.Draft.Content, copy.Draft.Content);
        await workspace.Templates.SaveDraftAsync(copy, copy.Draft with { Content = WritingProfile.Empty });
        Assert.Equal(Draft().Content, (await workspace.Templates.ReadAsync(source.Id)).Draft.Content);
    }
    [Fact]
    public async Task 文档必须预览最新差异后采用并可把本书规范另存模板()
    {
        await using var workspace = new TestWorkspace();
        await workspace.Templates.PublishAsync(await workspace.Templates.CreateAsync(Draft()));
        await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("小说创作"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.ProfileStyle = "作者自己的文风"; document.UseStyle = false;
        document.SelectedTemplateChoice = Assert.Single(document.TemplateChoices);
        Assert.False(document.CanApplyTemplate); await document.PreviewTemplateCommand.ExecuteAsync(null); Assert.True(document.CanApplyTemplate);
        document.ProfileWorld = "预览后作者又修改了"; Assert.False(document.CanApplyTemplate);
        await document.PreviewTemplateCommand.ExecuteAsync(null); await document.ApplyTemplateCommand.ExecuteAsync(null);
        Assert.Equal("雾港与未来邮局", document.ProfileWorld); Assert.Equal("作者自己的文风", document.ProfileStyle);
        document.ChapterText = "正文中的实时故事事件，不能写进模板";
        await document.SaveAsTemplateCommand.ExecuteAsync(null);
        var created = (await workspace.Templates.ListAsync()).Single(a => a.Draft.Name.EndsWith("创作模板"));
        Assert.Single(created.Versions); Assert.DoesNotContain(document.ChapterText, JsonSerializer.Serialize(created));
    }
    [Fact]
    public async Task 模板面板的草案保存发布重建和退出落盘可用()
    {
        await using var workspace = new TestWorkspace();
        var tool = new TemplateLibraryTool(workspace.Templates, workspace.Closing); await tool.InitializeAsync();
        tool.Name = "叙事方法模板"; tool.World = "第一座城市";
        await tool.PublishVersionCommand.ExecuteAsync(null);
        Assert.False(tool.IsDirty); Assert.Contains("1 个", tool.VersionStatus);
        tool.Style = "保存后隐藏面板仍保留这段编辑";
        Assert.True(tool.IsDirty); Assert.False(tool.CanNavigate);
        Assert.True(await tool.SaveBeforeCloseAsync()); await tool.DisposeAsync();
        await using var reopened = new TemplateLibraryTool(workspace.Templates, workspace.Closing); await reopened.InitializeAsync();
        reopened.SelectedTemplate = Assert.Single(reopened.Templates);
        Assert.Equal("保存后隐藏面板仍保留这段编辑", reopened.Style);
        var stored = Assert.Single(await workspace.Templates.ListAsync());
        Assert.Equal("", stored.Versions[0].Content.Style);
    }
    [Fact]
    public async Task 草案保存失败会留下可恢复副本且恢复不覆盖源模板()
    {
        await using var workspace = new TestWorkspace();
        var tool = new TemplateLibraryTool(workspace.Templates, workspace.Closing); await tool.InitializeAsync(); tool.Name = "失败恢复";
        await tool.SaveDraftCommand.ExecuteAsync(null); tool.World = "必须恢复的世界观";
        File.SetAttributes(workspace.Paths.Catalog, FileAttributes.ReadOnly);
        try { Assert.True(await tool.SaveBeforeCloseAsync()); await tool.DisposeAsync(); }
        finally { File.SetAttributes(workspace.Paths.Catalog, FileAttributes.Normal); }
        var recovery = Assert.Single(await workspace.Templates.ListRecoveryAsync());
        var restored = await workspace.Templates.RestoreAsCopyAsync(recovery.Id);
        Assert.Equal("必须恢复的世界观", restored.Draft.Content.World);
        Assert.Equal(2, (await workspace.Templates.ListAsync()).Count);
        Assert.NotEmpty(await workspace.Templates.ListRecoveryAsync());
    }
    [Fact]
    public async Task 损坏恢复草案不会遮蔽正常草案()
    {
        await using var workspace = new TestWorkspace(); var asset = TemplateAsset.Create(Draft());
        var recovery = new TemplateDraftRecovery(Guid.NewGuid(), 1, asset, 1);
        await workspace.Templates.WriteRecoveryAsync(recovery);
        var broken = Path.Combine(workspace.Paths.Root, "TemplateRecovery", Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(broken, "{broken");
        var missingAsset = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(workspace.Paths.Root, "TemplateRecovery", missingAsset.ToString("N") + ".json"),
            JsonSerializer.Serialize(new { Id = missingAsset, FormatVersion = 1, Asset = (object?)null, EditGeneration = 0 }));
        var entries = await workspace.Templates.ListRecoveryAsync();
        Assert.Equal(3, entries.Count); Assert.Single(entries, e => e.CanRead); Assert.Equal(2, entries.Count(e => !e.CanRead));
    }
    /// <summary>仅在持久化端口阻塞或失败，实际存储仍是 SQLite，用于复现真实并发窗口。</summary>
    private sealed class ControlledTemplateStore(ITemplateStore inner) : ITemplateStore
    {
        public Action<TemplateAsset>? BeforeSave { get; set; }
        public Action? BeforeDeleteRecovery { get; init; }
        public TemplateAsset Save(TemplateAsset asset, long? expectedRevision) { BeforeSave?.Invoke(asset); return inner.Save(asset, expectedRevision); }
        public IReadOnlyList<TemplateAsset> List() => inner.List();
        public TemplateAsset Read(Guid id) => inner.Read(id);
        public IReadOnlyList<TemplateRecoveryEntry> ListRecovery() => inner.ListRecovery();
        public TemplateDraftRecovery ReadRecovery(Guid id) => inner.ReadRecovery(id);
        public void WriteRecovery(TemplateDraftRecovery recovery) => inner.WriteRecovery(recovery);
        public void DeleteRecovery(Guid id) { BeforeDeleteRecovery?.Invoke(); inner.DeleteRecovery(id); }
    }
}
