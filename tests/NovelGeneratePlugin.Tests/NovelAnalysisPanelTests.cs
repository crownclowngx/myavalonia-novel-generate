using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Infrastructure.Export;
using NovelGeneratePlugin.Infrastructure.Import;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class NovelAnalysisPanelTests
{
    private static NovelAnalysisPanel Panel(TestWorkspace workspace, NovelReportTests.Context context, NovelAnalysisActivity activity)
    {
        var sources = new ReferenceSourceStore(workspace.Paths);
        return new(new(new TxtSourceReader(), sources), sources, context.Runner, activity,
            new(sources, context.Store, workspace.Models, new NovelAnalysisNodePreparer()), workspace.Connections,
            new(context.Reports, new AnalysisReportWriter(), workspace.Interaction), workspace.Interaction, workspace.Closing);
    }

    [Fact]
    public async Task 导入需有效预览且输入修改后不能提交旧快照()
    {
        await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
        await using var activity = new NovelAnalysisActivity(context.Runner, workspace.Closing); await using var panel = Panel(workspace, context, activity);
        await panel.InitializeAsync(); workspace.Interaction.NextPath = context.InputPath; await panel.ChooseFileCommand.ExecuteAsync(null);
        Assert.Equal(context.InputPath, panel.FilePath); await panel.PreviewCommand.ExecuteAsync(null); Assert.True(panel.CanImport); Assert.Equal(3, panel.Sections.Count);
        panel.EncodingName = "utf-16le"; Assert.False(panel.CanImport); Assert.Empty(panel.Sections);
        panel.EncodingName = "utf-8"; await panel.PreviewCommand.ExecuteAsync(null); File.Delete(context.InputPath);
        await panel.ImportCommand.ExecuteAsync(null); Assert.Equal(2, panel.Books.Count); Assert.False(panel.CanImport); Assert.Contains("全文来源已保存", panel.Status);
    }

    [Fact]
    public async Task 全文报告独立运行并显示真实完成覆盖与累计费用()
    {
        await using var workspace = new TestWorkspace(); var model = new NovelReportTests.Router(); var context = await NovelReportTests.Setup(workspace, model);
        await using var activity = new NovelAnalysisActivity(context.Runner, workspace.Closing); await using var panel = Panel(workspace, context, activity);
        await panel.InitializeAsync(); Assert.True(panel.CanStart); await panel.StartCommand.ExecuteAsync(null);
        var id = panel.SelectedRun!.Run.Id; await activity.StartAsync(id); await panel.ReadRunCommand.ExecuteAsync(null);
        Assert.Equal(AnalysisRunState.Completed, panel.SelectedRun!.Run.State); Assert.Contains("报告候选完成", panel.ProgressSummary);
        Assert.Contains("未知 0 次", panel.UsageSummary); Assert.All(panel.Nodes, item => Assert.Contains("已保存", item));
        await panel.ReadReportCommand.ExecuteAsync(null); Assert.Equal(7, panel.Report.Topics.Count); Assert.NotEmpty(panel.Report.Evidence);
        Assert.Empty(workspace.Catalog.List());
    }

    [Fact]
    public async Task 同一运行重复启动共用任务且暂停保留完整节点()
    {
        await using var workspace = new TestWorkspace(); var model = new HeldModel(); var context = await NovelReportTests.Setup(workspace, model);
        await using var activity = new NovelAnalysisActivity(context.Runner, workspace.Closing);
        activity.Changed += _ => throw new IOException("展示订阅者已关闭");
        var task = activity.StartAsync(context.Run.Id); await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(task, activity.StartAsync(context.Run.Id)); activity.Pause(context.Run.Id); model.Release.TrySetResult();
        var paused = await task; Assert.Equal(AnalysisRunState.Paused, paused.State); Assert.Single(paused.Nodes.Where(n => n.State == AnalysisNodeState.Completed));
        Assert.False(activity.IsActive(paused.Id)); Assert.Equal(AnalysisRunState.Completed, (await activity.StartAsync(paused.Id)).State);
    }

    [Fact]
    public async Task 关闭排空取消请求且未知费用须明确复核才能继续()
    {
        await using var workspace = new TestWorkspace(); var model = new HeldModel(); var context = await NovelReportTests.Setup(workspace, model);
        await using var activity = new NovelAnalysisActivity(context.Runner, workspace.Closing); await using var panel = Panel(workspace, context, activity);
        await panel.InitializeAsync(); var task = activity.StartAsync(context.Run.Id); await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await panel.SaveBeforeCloseAsync()); Assert.Equal(AnalysisRunState.Cancelled, (await task).State); Assert.True(model.Stopped);
        await panel.ReadRunCommand.ExecuteAsync(null); Assert.Contains("未知 1 次", panel.UsageSummary);
        await panel.ResumeCommand.ExecuteAsync(null); Assert.Contains("确认额外成本", panel.Status); Assert.Single(context.Runner.Usage(context.Run.Id));
        panel.AcknowledgeCosts = true; model.Release.TrySetResult(); await panel.ResumeCommand.ExecuteAsync(null); await activity.StartAsync(context.Run.Id);
        Assert.Equal(AnalysisRunState.Completed, context.Runner.Read(context.Run.Id).State); Assert.True(context.Runner.Usage(context.Run.Id)[0].RetryAcknowledged);
    }

    [Fact]
    public async Task 双运行切换保持各自检查点且预算不足不会发出请求()
    {
        await using var workspace = new TestWorkspace(); var model = new HeldModel(); var first = await NovelReportTests.Setup(workspace, model);
        var second = await NovelReportTests.Setup(workspace, model); await using var activity = new NovelAnalysisActivity(first.Runner, workspace.Closing);
        await using var panel = Panel(workspace, first, activity); await panel.InitializeAsync(); panel.MaximumRequests = 1;
        await panel.StartCommand.ExecuteAsync(null); Assert.Contains("超过总额", panel.Status); Assert.Empty(first.Runner.Usage(first.Run.Id));
        var taskA = activity.StartAsync(first.Run.Id); await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); var taskB = activity.StartAsync(second.Run.Id);
        panel.SelectedBook = panel.Books.Single(b => b.Book.Id == second.Run.BookId); await panel.LoadBookCommand.ExecuteAsync(null);
        Assert.Equal(second.Run.Id, panel.SelectedRun!.Run.Id); Assert.True(activity.IsActive(first.Run.Id)); Assert.True(activity.IsActive(second.Run.Id));
        model.Release.TrySetResult(); await Task.WhenAll(taskA, taskB); await panel.ReadRunCommand.ExecuteAsync(null);
        Assert.Equal(second.Run.Id, panel.SelectedRun!.Run.Id); Assert.Equal(AnalysisRunState.Completed, panel.SelectedRun.Run.State);
        Assert.Equal(AnalysisRunState.Completed, first.Runner.Read(first.Run.Id).State);
        // 与真实 Host 一致：Shutdown 先排空，再同步 Dispose 根服务，不能抛出“仅实现异步释放”。
        await workspace.Closing.ShutdownAsync(default); ((IDisposable)panel).Dispose(); ((IDisposable)activity).Dispose();
    }

    [Fact]
    public async Task 只读候选复核返回具体结构错误且不修改费用和运行()
    {
        await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new InvalidModel());
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default); var original = context.Runner.Read(context.Run.Id);
        var service = new NovelAnalysisCandidateService(new ReferenceSourceStore(workspace.Paths), context.Store, workspace.Models, new NovelAnalysisNodePreparer());
        var review = service.Read(context.Run.Id); Assert.NotEmpty(review.Validation); Assert.Equal("{}", review.Entry.PartialText);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(context.Runner.Read(context.Run.Id))); Assert.False(review.Entry.RetryAcknowledged); Assert.Single(context.Runner.Usage(context.Run.Id));
    }

    [Fact]
    public async Task 报告删除原TXT后定位导出且已有文件与取消路径保持不变()
    {
        await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default); File.Delete(context.InputPath);
        var reader = new NovelReportReader(context.Reports, new AnalysisReportWriter(), workspace.Interaction); await reader.LoadAsync(context.Run.Id);
        await reader.LocateCommand.ExecuteAsync(null); Assert.Equal(reader.SelectedEvidence!.Evidence.Quote, reader.SourceText[reader.SelectionStart..reader.SelectionEnd]);
        var path = Path.Combine(workspace.Root, "报告.md"); workspace.Interaction.NextPath = path; await reader.ExportCommand.ExecuteAsync(null);
        var saved = await File.ReadAllTextAsync(path); Assert.Contains(context.Reports.Read(context.Run.Id).Version, saved);
        await reader.ExportCommand.ExecuteAsync(null); Assert.Contains("已存在", reader.Status); Assert.Equal(saved, await File.ReadAllTextAsync(path));
        workspace.Interaction.NextPath = null; await reader.ExportCommand.ExecuteAsync(null); Assert.Contains("已取消", reader.Status); Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
        reader.Clear(); Assert.Empty(reader.Topics); Assert.Empty(reader.SourceText); Assert.False(reader.CanExport);
    }

    [Theory]
    [InlineData(false, 1d, 720)]
    [InlineData(true, 1.5d, 420)]
    public async Task 原生报告专题和证据绑定在明暗主题及DPI下可用(bool dark, double scale, int width)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
            await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
            await using var activity = new NovelAnalysisActivity(context.Runner, workspace.Closing); await using var panel = Panel(workspace, context, activity);
            var view = new NovelAnalysisView { DataContext = panel }; var window = new Window { Width = width, Height = 1000, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            try
            {
                window.Show(); window.SetRenderScaling(scale); await panel.InitializeAsync(); await panel.ReadReportCommand.ExecuteAsync(null);
                view.FindControl<TabControl>("AnalysisTabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var reportView = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<NovelReportView>().Single();
                var selector = reportView.FindControl<ComboBox>("AnalysisTopicSelector")!; Assert.Equal(7, selector.ItemCount); selector.SelectedIndex = 5;
                Assert.Equal(panel.Report.Topics[5], panel.Report.SelectedTopic); await panel.Report.LocateCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
                var excerpt = reportView.FindControl<TextBox>("AnalysisSourceExcerpt")!; Assert.Equal(panel.Report.SourceText, excerpt.Text);
                Assert.Equal(panel.Report.SelectedEvidence!.Evidence.Quote, excerpt.SelectedText); Assert.True(view.Bounds.Width > 300);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, $"novel-analysis-{(dark ? "dark-150" : "light-100")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
            return true;
        }, default);
    }

    [Fact]
    public async Task 隐藏原生工具并切换另一本书不取消独立分析()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var model = new HeldModel(); var context = await NovelReportTests.Setup(workspace, model);
            await using var activity = new NovelAnalysisActivity(context.Runner, workspace.Closing); await using var panel = Panel(workspace, context, activity);
            var view = new NovelAnalysisView { DataContext = panel }; var window = new Window { Width = 700, Height = 1000, Content = view };
            try
            {
                window.Show(); await panel.InitializeAsync(); var task = activity.StartAsync(context.Run.Id); await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                window.Content = new TextBlock { Text = "另一个工具" }; panel.FilePath = context.InputPath; await panel.PreviewCommand.ExecuteAsync(null); await panel.ImportCommand.ExecuteAsync(null);
                Assert.NotEqual(context.Run.BookId, panel.SelectedBook!.Book.Id); Assert.True(activity.IsActive(context.Run.Id));
                model.Release.TrySetResult(); Assert.Equal(AnalysisRunState.Completed, (await task).State);
                window.Content = new NovelAnalysisView { DataContext = panel }; panel.SelectedBook = panel.Books.Single(b => b.Book.Id == context.Run.BookId); await panel.LoadBookCommand.ExecuteAsync(null);
                Assert.Equal(AnalysisRunState.Completed, panel.SelectedRun!.Run.State); await panel.ReadReportCommand.ExecuteAsync(null); Assert.Equal(7, panel.Report.Topics.Count);
            }
            finally { window.Close(); }
            return true;
        }, default);
    }

    private sealed class HeldModel : ITextModel
    {
        private readonly NovelReportTests.Router _router = new();
        private readonly object _sync = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); lock (_sync) return _router.GenerateAsync(request, progress, cancellationToken).GetAwaiter().GetResult(); }
            finally { Stopped = true; }
        }
    }
    private sealed class InvalidModel : ITextModel
    { public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken) => Task.FromResult(new TextModelResponse("{}", ModelCompletion.Complete, new(100, 100))); }
}
