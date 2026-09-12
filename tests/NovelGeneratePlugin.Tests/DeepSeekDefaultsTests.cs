using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.ModelConnections;
using Xunit;
namespace NovelGeneratePlugin.Tests;

/// <summary>覆盖默认表单到持久化连接的完整边界：不调用网络、不改已有配置、Key 独立保存。</summary>
public sealed class DeepSeekDefaultsTests
{
    [Fact]
    public async Task 新建默认DeepSeek仅密钥为空且不自动落盘()
    {
        await using var workspace = new TestWorkspace();
        await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, workspace.Models);
        await tool.InitializeAsync();
        Assert.Equal(ModelProvider.DeepSeek, tool.Provider); Assert.Equal("https://api.deepseek.com", tool.Endpoint);
        Assert.Equal("DeepSeek 默认连接", tool.Name); Assert.Empty(tool.SecretInput); Assert.False(tool.IsDirty);
        Assert.All(tool.Presets, p => { Assert.Equal(new("deepseek-flash", 65536, "high"), p.Capture()); Assert.Contains("deepseek-v4-pro", p.ModelChoices); Assert.Equal(new[] { "none", "low", "high", "max" }, p.EffortChoices); });
        Assert.Empty((await workspace.Connections.ListAsync()).Connections);
        await tool.SaveConnectionCommand.ExecuteAsync(null);
        var saved = Assert.Single((await workspace.Connections.ListAsync()).Connections);
        Assert.Equal(CredentialState.Missing, await workspace.Connections.StateAsync(ConnectionService.Bind(saved)));
    }
    [Fact]
    public async Task 仅填写Key即可一次保存且刷新不产生新版本()
    {
        await using var workspace = new TestWorkspace();
        await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, workspace.Models);
        await tool.InitializeAsync(); tool.SecretInput = "unit-default-only-key";
        await tool.SaveConnectionCommand.ExecuteAsync(null);
        var saved = Assert.Single((await workspace.Connections.ListAsync()).Connections);
        Assert.Equal(ConnectionDefaults.Create(ModelProvider.DeepSeek), saved.Settings);
        Assert.Empty(tool.SecretInput); Assert.Contains("API Key 已保存", tool.Status);
        Assert.Equal(CredentialState.Session, await workspace.Connections.StateAsync(ConnectionService.Bind(saved)));
        await tool.RefreshCommand.ExecuteAsync(null); Assert.False(tool.IsDirty);
        Assert.Equal(saved, Assert.Single((await workspace.Connections.ListAsync()).Connections));
    }
    [Fact]
    public async Task 已有自定义连接原样加载而默认恢复须明确保存()
    {
        await using var workspace = new TestWorkspace();
        var custom = new ModelPreset("custom-compatible-model", 4096, "medium");
        var settings = new ConnectionSettings("作者已有连接", ModelProvider.DeepSeek, "https://example.test/v1", "", custom, custom, custom);
        var existing = await workspace.Connections.SaveAsync(null, settings);
        await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, workspace.Models);
        await tool.InitializeAsync(); tool.SelectedConnection = Assert.Single(tool.Connections); await tool.SaveBeforeCloseAsync();
        Assert.False(tool.IsDirty); Assert.Equal(settings.Endpoint, tool.Endpoint);
        Assert.All(tool.Presets, p => { Assert.Equal(custom, p.Capture()); Assert.Contains(custom.Model, p.ModelChoices); Assert.Contains("medium", p.EffortChoices); });
        tool.ApplyProviderDefaultsCommand.Execute(null);
        Assert.Equal("作者已有连接", tool.Name); Assert.True(tool.IsDirty);
        Assert.Equal(existing, Assert.Single((await workspace.Connections.ListAsync()).Connections));
        tool.ResetConfigurationCommand.Execute(null); Assert.Equal(settings.Endpoint, tool.Endpoint);
        Assert.All(tool.Presets, p => Assert.Equal(custom, p.Capture()));
    }
    [Fact]
    public async Task 主动切换服务商更新选项并隔离尚未提交密钥()
    {
        await using var workspace = new TestWorkspace();
        await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing, workspace.Models);
        await tool.InitializeAsync(); tool.SecretInput = "unit-input-not-sent"; tool.Provider = ModelProvider.CodexCli;
        Assert.Empty(tool.SecretInput); Assert.Empty(tool.Endpoint);
        Assert.All(tool.Presets, p => { Assert.Equal("gpt-6-astra", p.Model); Assert.Equal(new[] { "low", "medium", "high" }, p.EffortChoices); });
        tool.Provider = ModelProvider.DeepSeek;
        Assert.All(tool.Presets, p => { Assert.Equal("deepseek-flash", p.Model); Assert.Equal(65536, p.MaxOutputTokens); });
        tool.ResetConfigurationCommand.Execute(null);
    }
    [Fact]
    public void 下拉清空不抹去自定义模型且DeepSeek选项不进入Codex()
    {
        var editor = new PresetEditor("规划"); editor.Load(new("custom", 8192, "medium")); editor.ConfigureChoices(ModelProvider.DeepSeek);
        editor.SelectedModel = null; editor.SelectedEffort = null;
        Assert.Equal("custom", editor.Model); Assert.Equal("medium", editor.ReasoningEffort);
        editor.SelectedModel = "deepseek-v4-pro"; editor.SelectedEffort = "max";
        Assert.Equal(new("deepseek-v4-pro", 8192, "max"), editor.Capture());
        var api = ConnectionDefaults.Create(ModelProvider.DeepSeek);
        (api with { Checking = editor.Capture() }).Validate();
        var codex = api with { Provider = ModelProvider.CodexCli, Endpoint = "", CodexExecutable = @"C:\test\codex.exe", Checking = editor.Capture() };
        Assert.Throws<InvalidDataException>(codex.Validate);
    }
}
