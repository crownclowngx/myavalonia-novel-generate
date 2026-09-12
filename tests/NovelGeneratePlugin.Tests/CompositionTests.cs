using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Constants;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Plugin;
using NovelGeneratePlugin.Standalone;
using Xunit;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Features.ModelConnections;
namespace NovelGeneratePlugin.Tests;

public sealed class CompositionTests
{
    [Fact]
    public async Task 模块保留身份移除演示命令且文档隔离()
    {
        await using var workspace = new TestWorkspace();
        var registration = new PreviewRegistration();
        registration.Services.AddSingleton(workspace.Paths);
        registration.Services.AddSingleton<IPluginWindowInteraction>(workspace.Interaction);
        new NovelGeneratePluginModule().Configure(registration);
        Assert.Equal(PluginIds.MainDocument, Assert.Single(registration.Documents).Descriptor.DocumentTypeId);
        Assert.Equal(7, registration.Commands.Count); Assert.Equal(7, registration.Menus.Count);
        Assert.All(registration.Menus, m => Assert.StartsWith(PluginIds.Plugin.Value + ".command-placement.", m.PlacementId.Value));
        Assert.All(registration.CommandTargets, c => Assert.Equal(PluginIds.MainDocument, c.Target));
        Assert.All(registration.Commands, c => Assert.Contains(NovelCommands.All, n => n.Id == c.CommandId));
        Assert.Equal(typeof(NovelGeneratePlugin.Application.Projects.PluginCloseCoordinator), Assert.Single(registration.Lifecycles));
        Assert.Equal(2, registration.Tools.Count);
        Assert.Single(registration.Tools, t => t.Descriptor.ToolTypeId == PluginIds.Templates);
        Assert.Single(registration.Tools, t => t.Descriptor.ToolTypeId == PluginIds.Connections);
        await using var provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope1 = provider.CreateAsyncScope();
        await using var scope2 = provider.CreateAsyncScope();
        var first = scope1.ServiceProvider.GetRequiredService<MainDocument>();
        var second = scope2.ServiceProvider.GetRequiredService<MainDocument>();
        // 文档按作用域隔离，模板 Tool 由模块注册为单例；重建视图不能重建草案状态。
        var sharedTool = scope1.ServiceProvider.GetRequiredService<TemplateLibraryTool>();
        Assert.Same(sharedTool, scope2.ServiceProvider.GetRequiredService<TemplateLibraryTool>());
        Assert.Single(registration.Services, d => d.ServiceType == typeof(TemplateLibraryTool));
        Assert.Same(scope1.ServiceProvider.GetRequiredService<ModelConnectionsTool>(), scope2.ServiceProvider.GetRequiredService<ModelConnectionsTool>());
        Assert.Single(registration.Services, d => d.ServiceType == typeof(ModelConnectionsTool));
        await first.InitializeAsync(new NewDocumentActivation("作品甲"), CancellationToken.None);
        first.BookTitle = "甲书";
        Assert.Equal("未命名小说", second.BookTitle);
        Assert.Equal("作品甲", first.Presentation.Title);
        Assert.False(typeof(IPersistablePluginDocument).IsAssignableFrom(typeof(MainDocument)));
    }
    [Fact]
    public async Task 取消或释放后初始化不修改状态()
    {
        await using var workspace = new TestWorkspace();
        var document = workspace.CreateDocument();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await document.InitializeAsync(new NewDocumentActivation("不修改"), new CancellationToken(true)));
        Assert.Equal("小说创作", document.Presentation.Title);
        await document.DisposeAsync();
        await document.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await document.InitializeAsync(new NewDocumentActivation("已关闭"), CancellationToken.None));
    }
}
