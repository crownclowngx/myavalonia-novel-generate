using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Features.ModelConnections;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(NovelGeneratePlugin.Tests.TestAppBuilder))]
namespace NovelGeneratePlugin.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class TestApplication : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>在真实 Avalonia 控件树上输入中文并切换章节，补充纯 ViewModel 测试无法覆盖的双向绑定风险。</summary>
public sealed class NativeViewTests
{
    [Fact]
    public async Task DeepSeek默认下拉真实绑定且切换服务商不遗留错误参数()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, workspace.Models);
            var view = new ModelConnectionsView { DataContext = tool }; var window = new Window { Width = 720, Height = 1100, Content = view };
            try
            {
                window.Show(); await tool.InitializeAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.False(tool.IsDirty); Assert.Equal("", view.FindControl<TextBox>("SecretEditor")!.Text);
                var model = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ModelSelector");
                var effort = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "EffortSelector");
                Assert.Equal("deepseek-flash", model.SelectedItem); Assert.Equal("high", effort.SelectedItem);
                model.SelectedItem = "deepseek-v4-pro"; effort.SelectedItem = "none";
                Assert.Equal("deepseek-v4-pro", tool.Presets[0].Model); Assert.Equal("none", tool.Presets[0].ReasoningEffort);
                var provider = view.FindControl<ComboBox>("ProviderSelector")!;
                provider.SelectedItem = Domain.ModelProvider.CodexCli; Dispatcher.UIThread.RunJobs(); Assert.Equal("gpt-6-astra", tool.Presets[0].Model);
                provider.SelectedItem = Domain.ModelProvider.DeepSeek; Dispatcher.UIThread.RunJobs(); Assert.Equal("deepseek-flash", tool.Presets[0].Model);
                Assert.Equal("high", tool.Presets[0].ReasoningEffort); Assert.Equal(65536, tool.Presets[0].MaxOutputTokens);
                tool.ResetConfigurationCommand.Execute(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.False(tool.IsDirty);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, "deepseek-defaults.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally { tool.ResetConfigurationCommand.Execute(null); window.Close(); }
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task 连接表单真实绑定可输入中文和预设且密钥框始终遮蔽()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, workspace.Models);
            var view = new ModelConnectionsView { DataContext = tool }; var window = new Window { Width = 650, Height = 850, Content = view };
            try
            {
                window.Show(); await tool.InitializeAsync(); tool.Provider = Domain.ModelProvider.DeepSeek; tool.Endpoint = "https://api.deepseek.com";
                foreach (var preset in tool.Presets) preset.Model = "deepseek-flash";
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var name = view.FindControl<TextBox>("ConnectionNameEditor")!; name.Focus(); name.SelectAll(); window.KeyTextInput("独立测试连接");
                Assert.Equal("独立测试连接", tool.Name);
                var count = view.GetVisualDescendants().OfType<NumericUpDown>().First(); count.Value = 4096;
                Assert.Equal(4096, tool.Presets[0].MaxOutputTokens);
                Assert.NotEqual(default, view.FindControl<TextBox>("SecretEditor")!.PasswordChar);
                await tool.SaveConfigurationCommand.ExecuteAsync(null);
                Assert.Equal(4096, Assert.Single((await workspace.Connections.ListAsync()).Connections).Settings.Planning.MaxOutputTokens);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, "model-connections.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task 模板原生输入与共享模型在视图重建后保留草案()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace();
            await using var tool = new TemplateLibraryTool(workspace.Templates, workspace.Closing);
            var view = new TemplateLibraryView { DataContext = tool };
            var window = new Window { Width = 650, Height = 780, Content = view };
            try
            {
                window.Show(); await tool.InitializeAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var editor = view.FindControl<TextBox>("TemplateWorldEditor")!;
                editor.Focus(); window.KeyTextInput("雨水驱动的城市，记忆只能保存七天。");
                Assert.Contains("记忆", tool.World); Assert.True(tool.IsDirty);
                // Hide 生命周期由 Host 控制；此处只验证共享模型跨视图重建保持编辑且真实控件正确重新绑定。
                view = new TemplateLibraryView { DataContext = tool }; window.Content = view;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(tool.World, view.FindControl<TextBox>("TemplateWorldEditor")!.Text);
                await tool.PublishVersionCommand.ExecuteAsync(null);
                Assert.Equal(tool.World, Assert.Single((await workspace.Templates.ListAsync()).Single().Versions).Content.World);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, "template-library.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task 原生故事实体输入和上下文预览可用()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
            var view = new MainView { DataContext = document }; var window = new Window { Width = 1200, Height = 900, Content = view };
            try
            {
                window.Show(); await document.InitializeAsync(new NewDocumentActivation("实体测试"), default);
                workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
                view.FindControl<TabControl>("InspectorTabs")!.SelectedIndex = 2; view.FindControl<Expander>("StoryExpander")!.IsExpanded = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var editor = view.FindControl<TextBox>("EntityNameEditor")!; editor.BringIntoView(); editor.Focus(); window.KeyTextInput("林舟"); Assert.Equal("林舟", document.EntityName);
                document.EntityAliases = "阿舟"; await document.SaveStoryEntityCommand.ExecuteAsync(null); await document.PreviewStoryContextCommand.ExecuteAsync(null);
                Assert.Contains("当前上下文", document.StoryContextStatus); Assert.Single(workspace.Store.Read(document.ProjectPath).Project.Story.Entities);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, "story-context.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task 原生规划字段中文输入保存并重建视图()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
            var view = new MainView { DataContext = document }; var window = new Window { Width = 1200, Height = 1000, Content = view };
            try
            {
                window.Show(); await document.InitializeAsync(new NewDocumentActivation("规划测试"), default);
                workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
                view.FindControl<Expander>("PlanningExpander")!.IsExpanded = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var editor = view.FindControl<TextBox>("PlanGoalEditor")!; editor.BringIntoView(); editor.Focus(); window.KeyTextInput("找到雾港寄信人");
                Assert.Equal("找到雾港寄信人", document.PlanGoal); await document.SaveCommand.ExecuteAsync(null);
                Assert.Equal(document.PlanGoal, workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Plan.Goal);
                view = new MainView { DataContext = document }; window.Content = view; view.FindControl<Expander>("PlanningExpander")!.IsExpanded = true;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.Equal(document.PlanGoal, view.FindControl<TextBox>("PlanGoalEditor")!.Text);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, "planning.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task 原生单章候选独立显示问题定位与工作稿提交()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace);
            book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "作者的原始正文" }) };
            workspace.Store.Create(workspace.ProjectPath(), book);
            var review = ChapterGenerationTests.Review with { Issues = [new(Domain.ReviewSeverity.Advice, Domain.ReviewCategory.Style, "可考虑深化意象", "铜钥匙")] };
            var fake = new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Body), _ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(review)));
            var service = new Application.Models.ChapterGenerationService(workspace.Connections, new(fake, new Infrastructure.Persistence.ModelRequestStore(workspace.Paths)), new Infrastructure.Persistence.ChapterWorkStore(workspace.Paths));
            await using var document = workspace.CreateDocument(generation: service);
            var view = new MainView { DataContext = document }; var window = new Window { Width = 1200, Height = 1000, Content = view };
            try
            {
                window.Show(); await document.InitializeAsync(new NewDocumentActivation("单章测试"), default);
                workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
                document.GenerationTargetCharacters = 100; document.GenerationMaximumRepairs = 0;
                view.FindControl<Expander>("GenerationExpander")!.IsExpanded = true;
                await document.GenerateChapterCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal("作者的原始正文", document.ChapterText); Assert.True(document.CanCommitGeneration);
                document.SelectedGenerationIssue = Assert.Single(document.GenerationIssues); document.LocateGenerationIssueCommand.Execute(null);
                Dispatcher.UIThread.RunJobs(); var candidate = view.FindControl<TextBox>("GenerationCandidate")!;
                Assert.Equal("铜钥匙", candidate.Text![candidate.SelectionStart..candidate.SelectionEnd]);
                candidate.BringIntoView(); var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, "chapter-generation.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                await document.CommitGenerationCommand.ExecuteAsync(null);
                Assert.Equal(ChapterGenerationTests.Body, workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Text);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task 原生连续运行三章完成后显示用量并可读取记录()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace); workspace.Store.Create(workspace.ProjectPath(), book);
            var fake = new ScriptedTextModel(Enumerable.Range(0, 6).Select<int, Func<Application.Models.TextModelRequest, Application.Models.TextModelResponse>>(i => _ => ChapterGenerationTests.Response(i % 2 == 0 ? ChapterGenerationTests.Body : ChapterGenerationTests.Json(ChapterGenerationTests.Review))).ToArray());
            var requests = new Application.Models.ModelRequestService(fake, new Infrastructure.Persistence.ModelRequestStore(workspace.Paths));
            var works = new Infrastructure.Persistence.ChapterWorkStore(workspace.Paths); var planning = new Application.Models.PlanningService(workspace.Connections, requests);
            var generation = new Application.Models.ChapterGenerationService(workspace.Connections, requests, works);
            var runner = new Application.Models.ContinuousRunService(planning, generation, requests, works, new Infrastructure.Persistence.ContinuousRunStore(workspace.Paths));
            await using var document = workspace.CreateDocument(planning, generation, runner); var view = new MainView { DataContext = document }; var window = new Window { Width = 1200, Height = 1000, Content = view };
            try
            {
                window.Show(); await document.InitializeAsync(new NewDocumentActivation("连续运行测试"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
                document.GenerationTargetCharacters = 100; document.GenerationMaximumRepairs = 0; view.FindControl<Expander>("ContinuousExpander")!.IsExpanded = true;
                await document.StartContinuousCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Contains("已完成", document.RunStatus); Assert.Contains("3/3", document.RunStatus); Assert.Contains("请求 6/20", document.RunUsage);
                Assert.Equal(3, workspace.Store.Read(document.ProjectPath).Project.Revisions.History.Length); await document.LoadContinuousCommand.ExecuteAsync(null); Assert.False(document.CanResumeRun);
                view.FindControl<Expander>("ContinuousExpander")!.BringIntoView(); var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, "continuous-run.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task 原生材料提炼证据定位及模板本书分别采用()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); await MaterialCalibrationTests.MaterialAsync(workspace);
            var fake = new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(MaterialCalibrationTests.Analysis(Domain.MaterialPurpose.Methods))));
            var service = new Application.Models.MaterialCalibrationService(new Infrastructure.Persistence.MaterialStore(workspace.Paths), workspace.Connections, new(fake, new Infrastructure.Persistence.ModelRequestStore(workspace.Paths)));
            await using var panel = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing);
            var view = new MaterialCalibrationView { DataContext = panel }; var window = new Window { Width = 700, Height = 1000, Content = new ScrollViewer { Content = view } };
            try
            {
                window.Show(); await panel.InitializeAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var input = view.FindControl<TextBox>("MaterialTextEditor")!; input.Focus(); window.KeyTextInput(MaterialCalibrationTests.SourceText); Assert.Equal(MaterialCalibrationTests.SourceText, panel.Text);
                panel.SelectedConnection = Assert.Single(panel.Connections); await panel.AnalyzeMaterialCommand.ExecuteAsync(null); Assert.Contains("期待", panel.Methods);
                panel.SelectedEvidence = Assert.Single(panel.EvidenceChoices); panel.LocateEvidenceCommand.Execute(null); Dispatcher.UIThread.RunJobs(); Assert.Contains("建立期待", input.Text![input.SelectionStart..input.SelectionEnd]);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS"); if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, "material-calibration.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
                await panel.PublishMaterialTemplateCommand.ExecuteAsync(null); Assert.Single(await workspace.Templates.ListAsync());
                await using var document = workspace.CreateDocument(); await document.InitializeAsync(new NewDocumentActivation("材料采用"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
                await document.RefreshBookMaterialsCommand.ExecuteAsync(null); document.SelectedBookMaterial = Assert.Single(document.BookMaterials); await document.AdoptBookMaterialCommand.ExecuteAsync(null);
                Assert.Single(workspace.Store.Read(document.ProjectPath).Project.MaterialSources); Assert.Single(await workspace.Templates.ListAsync());
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task 原生选区改写候选对比接受与历史复核()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var book = await ChapterGenerationTests.BookAsync(workspace);
            book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = ChapterGenerationTests.Body }) };
            workspace.Store.Create(workspace.ProjectPath(), book);
            var fake = new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(new Application.Models.SelectionReplacement("铁钥匙"))), _ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(ChapterGenerationTests.Review)));
            await using var document = workspace.CreateDocument(generation: new Application.Models.ChapterGenerationService(workspace.Connections, new(fake, new Infrastructure.Persistence.ModelRequestStore(workspace.Paths)), new Infrastructure.Persistence.ChapterWorkStore(workspace.Paths))); var view = new MainView { DataContext = document }; var window = new Window { Width = 1200, Height = 900, Content = view };
            try
            {
                window.Show(); await document.InitializeAsync(new NewDocumentActivation("改稿"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
                var editor = view.FindControl<TextBox>("ChapterEditor")!; document.GenerationTargetCharacters = 100;
                editor.SelectionStart = ChapterGenerationTests.Body.IndexOf("铜钥匙", StringComparison.Ordinal); editor.SelectionEnd = editor.SelectionStart + 3;
                await document.RewriteSelectionCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
                Assert.True(fake.Requests.Count == 2, document.Notice + " / " + document.GenerationStatus); Assert.Contains("铜钥匙", document.ChapterText); Assert.Contains("铁钥匙", document.GenerationText); Assert.Contains("原文", document.CandidateDifference);
                await document.CommitGenerationCommand.ExecuteAsync(null); Assert.Contains("铁钥匙", editor.Text);
                document.RefreshRevisionsCommand.Execute(null); document.SelectedRevision = Assert.Single(document.RevisionChoices); Assert.Equal("正文相同。", document.RevisionDifference);
                document.FinalizeFirstChapter = 1; document.FinalizeLastChapter = 1; document.ConfirmFinalizeRange = true; await document.FinalizeRangeCommand.ExecuteAsync(null);
                Assert.NotNull(workspace.Store.Read(document.ProjectPath).Project.Revisions.Head(book.Chapters[0].Id).FormalId);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task 窄窗高缩放切换侧栏再回正文保留长文本和选区()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
            var view = new MainView { DataContext = document }; var window = new Window { Width = 800, Height = 700, Content = view };
            try
            {
                window.Show(); window.SetRenderScaling(1.5); await document.InitializeAsync(new NewDocumentActivation("窄窗"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
                document.ChapterText = string.Concat(Enumerable.Repeat("雾港的夜雨落在信封上。\n", 2000)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var editor = view.FindControl<TextBox>("ChapterEditor")!; editor.SelectionStart = 2; editor.SelectionEnd = 5;
                var toggle = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("InspectorToggle")!; Assert.True(toggle.IsVisible); toggle.IsChecked = true;
                view.FindControl<TabControl>("InspectorTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(view.FindControl<Border>("InspectorPanel")!.IsVisible); Assert.False(view.FindControl<Grid>("ManuscriptPanel")!.IsVisible);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS"); if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, "narrow-inspector-150.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
                toggle.IsChecked = false; Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.True(editor.Bounds.Height > 200); Assert.Equal(2, editor.SelectionStart); Assert.Equal(5, editor.SelectionEnd);
                await document.SaveCommand.ExecuteAsync(null); Assert.Equal(document.ChapterText, workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Text);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }
    [Theory]
    [InlineData(1200, 850, false)]
    [InlineData(800, 650, true)]
    public async Task 原生编辑框输入和选章绑定在明暗主题下可用(int width, int height, bool dark)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () => { await VerifyViewAsync(width, height, dark); return true; }, CancellationToken.None);
    }
    private static async Task VerifyViewAsync(int width, int height, bool dark)
    {
        await using var workspace = new TestWorkspace();
        await using var document = workspace.CreateDocument();
        var view = new MainView { DataContext = document };
        var window = new Window { Width = width, Height = height, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            await document.InitializeAsync(new NewDocumentActivation("小说创作"), CancellationToken.None);
            document.BookTitle = "雾港来信"; document.Idea = "一个邮差收到写着自己死亡日期的信，决定在雾港寻找寄信人。";
            workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var editor = view.FindControl<TextBox>("ChapterEditor")!;
            Assert.True(editor.IsEffectivelyEnabled); Assert.True(editor.Bounds.Height >= 60);
            editor.Focus(); window.KeyTextInput("雾从码头涌来。\n阿宁停在邮局门前，信封上写着明天的日期。");
            Assert.Contains("阿宁", document.ChapterText);
            document.AddChapterCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            editor.Focus(); window.KeyTextInput("第二章的秘密");
            view.FindControl<ListBox>("ChapterList")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("阿宁", editor.Text); Assert.DoesNotContain("第二章的秘密", editor.Text);
            await document.SaveCommand.ExecuteAsync(null);
            Assert.Contains("阿宁", workspace.Store.Read(document.ProjectPath).Project.Chapters[0].Text);
            document.RuleOriginal = "原生定位测试"; document.RulePattern = "阿宁";
            await document.SaveRuleVersionCommand.ExecuteAsync(null); await document.CheckLocalRulesCommand.ExecuteAsync(null);
            document.SelectedFinding = Assert.Single(document.RuleFindings); document.LocateRuleFindingCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("阿宁", editor.Text![editor.SelectionStart..editor.SelectionEnd]);
            var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame); frame.Save(Path.Combine(output, $"workspace-{width}-{(dark ? "dark" : "light")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }
}
