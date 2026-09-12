using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Plugin;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class WorkbenchTests
{
    [Fact]
    public async Task 宿主命令只影响目标作品且未知命令和关闭后均拒绝()
    {
        await using var workspace = new TestWorkspace();
        await using var first = workspace.CreateDocument(); await using var second = workspace.CreateDocument();
        await first.InitializeAsync(new NewDocumentActivation("甲"), default); await second.InitializeAsync(new NewDocumentActivation("乙"), default);
        workspace.Store.Create(workspace.ProjectPath(), BookProject.Create("甲书")); workspace.Interaction.NextPath = workspace.ProjectPath();
        var target = (IWorkbenchDocumentCommandTarget)first; var other = (IWorkbenchDocumentCommandTarget)second;
        Assert.True(target.CanExecute(NovelCommands.Open)); Assert.False(target.CanExecute(NovelCommands.Start));
        var changes = new List<CommandId>(); target.CommandStateChanged += (_, e) => changes.Add(e.CommandId);
        await target.ExecuteAsync(NovelCommands.Open, default);
        Assert.Equal("甲书", first.BookTitle); Assert.False(second.HasProject); Assert.False(other.CanExecute(NovelCommands.Generate));
        Assert.Contains(NovelCommands.Generate, changes); Assert.DoesNotContain(changes, id => !NovelCommands.All.Any(c => c.Id == id));
        var unknown = new CommandId("other.command"); Assert.False(target.CanExecute(unknown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await target.ExecuteAsync(unknown, default));
        await first.DisposeAsync(); var count = changes.Count;
        Assert.All(NovelCommands.All, c => Assert.False(target.CanExecute(c.Id))); Assert.Equal(count, changes.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await target.ExecuteAsync(NovelCommands.Open, default));
    }
    [Fact]
    public async Task 宿主取消令牌传入模型并等待任务排空后返回()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); workspace.Store.Create(workspace.ProjectPath(), book);
        var fake = new BlockingModel(); var workStore = new ChapterWorkStore(workspace.Paths);
        await using var document = workspace.CreateDocument(generation: new(workspace.Connections, new(fake, new ModelRequestStore(workspace.Paths)), workStore));
        await document.InitializeAsync(new NewDocumentActivation("取消"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
        document.GenerationTargetCharacters = 100; var target = (IWorkbenchDocumentCommandTarget)document; using var cancellation = new CancellationTokenSource();
        var running = target.ExecuteAsync(NovelCommands.Generate, cancellation.Token).AsTask(); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(running.IsCompleted); Assert.True(target.CanExecute(NovelCommands.Cancel)); Assert.False(target.CanExecute(NovelCommands.Start));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(fake.Stopped); Assert.False(document.IsBusy); Assert.False(target.CanExecute(NovelCommands.Cancel));
        Assert.Equal(ChapterWorkState.Cancelled, Assert.Single(workStore.Recent(book.Id, book.Chapters[0].Id)).State);
        Assert.Empty(workspace.Store.Read(document.ProjectPath).Project.Revisions.History);
    }
    [Fact]
    public async Task 宿主取消动作可在生成命令执行中调用且不取消其他文档()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); workspace.Store.Create(workspace.ProjectPath(), book);
        var fake = new BlockingModel();
        await using var document = workspace.CreateDocument(generation: new(workspace.Connections, new(fake, new ModelRequestStore(workspace.Paths)), new ChapterWorkStore(workspace.Paths)));
        await using var other = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("取消"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
        document.GenerationTargetCharacters = 100; var target = (IWorkbenchDocumentCommandTarget)document;
        var running = target.ExecuteAsync(NovelCommands.Generate, default).AsTask(); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await target.ExecuteAsync(NovelCommands.Cancel, default); await running;
        Assert.True(fake.Stopped); Assert.False(other.IsBusy); Assert.Contains("已取消", document.GenerationStatus);
    }
    [Fact]
    public async Task 显示订阅异常不破坏保存且宿主能识别操作错误()
    {
        await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("错误"), default); var target = (IWorkbenchDocumentCommandTarget)document;
        target.CommandStateChanged += (_, _) => throw new InvalidOperationException("坏显示端");
        workspace.Interaction.NextPath = workspace.ProjectPath("不存在");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await target.ExecuteAsync(NovelCommands.Open, default));
        Assert.False(document.IsBusy); Assert.True(target.CanExecute(NovelCommands.Open));
    }
    [Fact]
    public async Task 忙碌切章被拒绝且输入不会写入其他章节()
    {
        await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); workspace.Store.Create(workspace.ProjectPath(), book);
        var fake = new BlockingModel();
        await using var document = workspace.CreateDocument(generation: new(workspace.Connections, new(fake, new ModelRequestStore(workspace.Paths)), new ChapterWorkStore(workspace.Paths)));
        await document.InitializeAsync(new NewDocumentActivation("切章"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
        var selected = document.SelectedChapter; document.GenerationTargetCharacters = 100;
        var running = document.GenerateChapterCommand.ExecuteAsync(null); await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        document.SelectedChapter = document.Chapters[1]; Assert.Equal(selected, document.SelectedChapter);
        document.CancelCreationCommand.Execute(null); await running; document.ChapterText = "第一章的人工输入"; await document.SaveCommand.ExecuteAsync(null);
        var stored = workspace.Store.Read(document.ProjectPath).Project; Assert.Equal("第一章的人工输入", stored.Chapters[0].Text); Assert.Empty(stored.Chapters[1].Text);
    }
    [Fact]
    public async Task 放弃和回退均保留编辑缓冲的摘要()
    {
        await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("摘要"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.ChapterText = "作者正文"; document.RevisionSummary = "作者手写的摘要"; await document.CommitDraftCommand.ExecuteAsync(null);
        await document.DiscardWorkingCommand.ExecuteAsync(null); Assert.Equal("作者手写的摘要", document.RevisionSummary);
        await document.CommitDraftCommand.ExecuteAsync(null); await document.FinalizeChapterCommand.ExecuteAsync(null); await document.RollbackFormalCommand.ExecuteAsync(null);
        Assert.Equal("作者手写的摘要", document.RevisionSummary); await document.SaveCommand.ExecuteAsync(null);
        Assert.Equal("作者手写的摘要", workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Summary);
    }
    [Fact]
    public async Task 宿主状态回调重入关闭也等待刚启动的操作()
    {
        await using var workspace = new TestWorkspace(); var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("重入"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.ChapterText = "关闭前必须保留"; Task? closing = null; var entered = false;
        ((IWorkbenchDocumentCommandTarget)document).CommandStateChanged += (_, _) =>
        {
            if (entered || !document.IsBusy) return;
            entered = true; closing = document.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        };
        await document.SaveCommand.ExecuteAsync(null); Assert.NotNull(closing); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("关闭前必须保留", workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Text);
        Assert.False(((IWorkbenchDocumentCommandTarget)document).CanExecute(NovelCommands.Open));
    }
    private sealed class BlockingModel : ITextModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("不应正常返回"); }
            finally { Stopped = true; }
        }
    }
}
