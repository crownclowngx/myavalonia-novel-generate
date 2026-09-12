using MyAvaloniaManagement.PluginSdk;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class DocumentEditingTests
{
    [Fact]
    public async Task 两书选中项值相等仍显式重新加载各自正文()
    {
        await using var workspace = new TestWorkspace();
        var a = NovelGeneratePlugin.Domain.BookProject.Create("甲书");
        a = a with { Chapters = a.Chapters.SetItem(0, a.Chapters[0] with { Text = "甲的正文" }) };
        var b = a with { Id = Guid.NewGuid(), Title = "乙书", Chapters = a.Chapters.SetItem(0, a.Chapters[0] with { Text = "乙的正文" }) };
        workspace.Store.Create(workspace.ProjectPath("甲"), a); workspace.Store.Create(workspace.ProjectPath("乙"), b);
        await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("小说创作"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath("甲"); await document.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal("甲的正文", document.ChapterText);
        workspace.Interaction.NextPath = workspace.ProjectPath("乙"); await document.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal("乙的正文", document.ChapterText);
        document.BookTitle = "乙书改名"; await document.SaveCommand.ExecuteAsync(null);
        Assert.Equal("乙的正文", workspace.Store.Read(workspace.ProjectPath("乙")).Project.Chapters[0].Text);
    }
    [Fact]
    public async Task 新建编辑换章加卷保存和重开通过真实应用入口完成()
    {
        await using var workspace = new TestWorkspace();
        var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("小说创作"), CancellationToken.None);
        document.BookTitle = "雾港来信"; document.Idea = "未来邮局";
        workspace.Interaction.NextPath = workspace.ProjectPath();
        await document.NewProjectCommand.ExecuteAsync(null);
        Assert.True(document.HasProject);
        document.ChapterText = "第一章正文"; var first = document.SelectedChapter;
        document.AddChapterCommand.Execute(null); document.ChapterText = "第二章正文";
        document.SelectedChapter = first; Assert.Equal("第一章正文", document.ChapterText);
        document.ChapterTitle = "午夜的投递";
        document.AddVolumeCommand.Execute(null); document.ChapterText = "第二卷开篇";
        await document.SaveCommand.ExecuteAsync(null);
        await document.DisposeAsync();
        await using var reopened = workspace.CreateDocument();
        await reopened.InitializeAsync(new NewDocumentActivation("小说创作"), CancellationToken.None);
        await reopened.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal("雾港来信", reopened.BookTitle); Assert.Equal("未来邮局", reopened.Idea);
        Assert.Equal(3, reopened.Chapters.Count); Assert.Equal("午夜的投递", reopened.ChapterTitle);
        Assert.Equal("第一章正文", reopened.ChapterText);
        reopened.SelectedChapter = reopened.Chapters[2]; Assert.Equal("第二卷开篇", reopened.ChapterText);
    }
    [Fact]
    public async Task 取消文件选择不丢当前编辑而打开失败不切换项目()
    {
        await using var workspace = new TestWorkspace();
        await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("小说创作"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.ChapterText = "保留的正文";
        workspace.Interaction.NextPath = null; await document.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal("保留的正文", document.ChapterText);
        workspace.Interaction.NextPath = workspace.ProjectPath("不存在"); await document.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(workspace.ProjectPath(), document.ProjectPath); Assert.Equal("保留的正文", document.ChapterText);
    }
}
