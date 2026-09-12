using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Features.ModelConnections;
using NovelGeneratePlugin.Plugin;
using NovelGeneratePlugin.Standalone;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class PluginCloseTests
{
    [Fact]
    public async Task 同步Scope关闭与异步插件Shutdown配合排空后可同步释放Provider()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var registration = new PreviewRegistration();
            registration.Services.AddSingleton(workspace.Paths); registration.Services.AddSingleton<IPluginWindowInteraction>(workspace.Interaction);
            new NovelGeneratePluginModule().Configure(registration);
            using var provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var lifecycle = provider.GetRequiredService<PluginCloseCoordinator>(); await lifecycle.InitializeAsync(CancellationToken.None);
            using var scope = provider.CreateScope(); var document = scope.ServiceProvider.GetRequiredService<MainDocument>();
            await document.InitializeAsync(new NewDocumentActivation("同步关闭"), CancellationToken.None);
            workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null); document.ChapterText = "窗口关闭前最后输入";
            var template = provider.GetRequiredService<TemplateLibraryTool>(); await template.InitializeAsync(); template.World = "模板关闭前最后输入";
            // 未显示过的共享 Tool 也必须能在根 Shutdown 之后由同步容器安全释放。
            provider.GetRequiredService<ModelConnectionsTool>();
            scope.Dispose();
            await lifecycle.ShutdownAsync(new CancellationToken(true));
            Assert.Equal("窗口关闭前最后输入", workspace.Store.Read(workspace.ProjectPath()).Project.Chapters[0].Text);
            Assert.Equal("模板关闭前最后输入", Assert.Single(await workspace.Templates.ListAsync()).Draft.Content.World);
            provider.Dispose();
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task 同步关闭任务失败被观察且Shutdown可重试而不假装完成()
    {
        await using var workspace = new TestWorkspace(); var attempts = 0;
        var owner = workspace.Closing.Register(() => ++attempts == 1 ? Task.FromException(new IOException("模拟保存失败")) : Task.CompletedTask, null);
        await Assert.ThrowsAsync<IOException>(() => owner.CloseAsync());
        await workspace.Closing.ShutdownAsync(CancellationToken.None); Assert.Equal(2, attempts);
        await owner.CloseAsync(); Assert.Equal(2, attempts);
    }
    [Fact]
    public async Task 关闭期间在途任务未结束时插件Shutdown不会提前返回()
    {
        await using var workspace = new TestWorkspace(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = workspace.Closing.Register(() => release.Task, null);
        var first = owner.CloseAsync(); Assert.Same(first, owner.CloseAsync());
        var shutdown = workspace.Closing.ShutdownAsync(CancellationToken.None); Assert.False(shutdown.IsCompleted);
        release.SetResult(); await shutdown; Assert.True(first.IsCompletedSuccessfully);
    }
}
