using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Constants;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Plugin;
using NovelGeneratePlugin.Standalone;
using Xunit;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Infrastructure.Persistence;
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
        Assert.Empty(registration.Commands);
        Assert.Empty(registration.Tools);
        await using var provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope1 = provider.CreateAsyncScope();
        await using var scope2 = provider.CreateAsyncScope();
        var first = scope1.ServiceProvider.GetRequiredService<MainDocument>();
        var second = scope2.ServiceProvider.GetRequiredService<MainDocument>();
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
