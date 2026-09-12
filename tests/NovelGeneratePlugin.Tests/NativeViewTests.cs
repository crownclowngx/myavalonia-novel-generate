using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Features.Main;
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
