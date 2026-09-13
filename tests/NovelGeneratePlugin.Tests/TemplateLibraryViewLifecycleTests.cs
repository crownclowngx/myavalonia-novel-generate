using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.TemplateLibrary;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class TemplateLibraryViewLifecycleTests
{
    [Fact]
    public async Task 宿主恢复布局先挂载视图再绑定模型时自动加载已保存模板()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace();
            var saved = await workspace.Templates.CreatePublishedAsync(new TemplateDraft("重开模板", [], new("", "已保存文风", "", ""), "本地验收"));
            await using var tool = new TemplateLibraryTool(workspace.Templates, workspace.Closing);
            var view = new TemplateLibraryView();
            var window = new Window { Content = view, Width = 800, Height = 800 };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                Assert.True(view.IsLoaded);
                view.DataContext = tool;
                for (var i = 0; i < 100 && tool.Templates.Count == 0; i++) await Task.Delay(20);
                Assert.Equal(saved.Id, Assert.Single(tool.Templates).Id);
                view.DataContext = null; view.DataContext = tool;
                Assert.Single(tool.Templates);
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
}
