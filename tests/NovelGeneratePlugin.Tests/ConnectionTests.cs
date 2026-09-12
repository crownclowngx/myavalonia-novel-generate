using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.ModelConnections;
using NovelGeneratePlugin.Infrastructure.Credentials;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public async Task 密钥写入失败保留输入供重试且不伪装保存成功()
    {
        await using var workspace = new TestWorkspace(); var fail = true;
        var vault = new ControlledVault(workspace.Vault) { BeforeSet = () => { if (fail) throw new IOException("注入密钥写入失败"); } };
        var service = new ConnectionService(new ConnectionStore(workspace.Paths), vault); await service.SaveAsync(null, Api());
        await using var tool = new ModelConnectionsTool(service, workspace.Closing); await tool.InitializeAsync();
        tool.SelectedConnection = Assert.Single(tool.Connections); await tool.SaveBeforeCloseAsync();
        tool.SecretInput = TestKey; await tool.SaveSecretCommand.ExecuteAsync(null);
        Assert.Equal(TestKey, tool.SecretInput); Assert.Contains("失败", tool.Status); Assert.False(await tool.SaveBeforeCloseAsync());
        fail = false; await tool.SaveSecretCommand.ExecuteAsync(null); Assert.Empty(tool.SecretInput); Assert.Contains("会话", tool.CredentialStatus);
    }
    [Fact]
    public async Task 旧授权代数的迟到保存和清除不会破坏新版凭据()
    {
        await using var workspace = new TestWorkspace(); var a = await workspace.Connections.SaveAsync(null, Api());
        using var anotherVault = new UserCredentialVault(workspace.Paths);
        var b = await workspace.Connections.SaveAsync(a, Api(endpoint: "https://example.org/v1"));
        anotherVault.Set(Target(b), "unit-new-endpoint-secret", true);
        // 稳定复现旧实例已通过配置检查、后来才进入凭据存储的时序。
        workspace.Vault.Set(Target(a), "unit-late-old-secret", true); workspace.Vault.Clear(Target(a));
        Assert.Equal("unit-new-endpoint-secret", anotherVault.ReadForRequest(Target(b)));
        anotherVault.Set(Target(b), "unit-new-session-secret", false);
        anotherVault.Set(Target(a), "unit-old-session", false); anotherVault.Clear(Target(a));
        Assert.Equal("unit-new-session-secret", anotherVault.ReadForRequest(Target(b)));
    }
    [Fact]
    public async Task 保存密钥期间的新输入保留且关闭必须等待处理()
    {
        await using var workspace = new TestWorkspace(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new ControlledVault(workspace.Vault) { BeforeSet = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var service = new ConnectionService(new ConnectionStore(workspace.Paths), vault);
        var connection = await service.SaveAsync(null, Api());
        await using var tool = new ModelConnectionsTool(service, workspace.Closing); await tool.InitializeAsync();
        tool.SelectedConnection = Assert.Single(tool.Connections); await tool.SaveBeforeCloseAsync();
        tool.SecretInput = "unit-first-secret"; var saving = tool.SaveSecretCommand.ExecuteAsync(null);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); tool.SecretInput = "unit-next-secret"; }
        finally { release.Set(); }
        await saving; Assert.Equal("unit-next-secret", tool.SecretInput); Assert.False(await tool.SaveBeforeCloseAsync());
        tool.ClearInputCommand.Execute(null); Assert.True(await tool.SaveBeforeCloseAsync());
        Assert.Equal("unit-first-secret", workspace.Vault.ReadForRequest(Target(connection)));
    }
    [Fact]
    public async Task 凭据查询期间拒绝切换避免旧状态写到另一连接()
    {
        await using var workspace = new TestWorkspace(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new ControlledVault(workspace.Vault) { BeforeState = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var service = new ConnectionService(new ConnectionStore(workspace.Paths), vault);
        var a = await service.SaveAsync(null, Api("甲")); var b = await service.SaveAsync(null, Api("乙"));
        await using var tool = new ModelConnectionsTool(service, workspace.Closing); await tool.InitializeAsync();
        tool.SelectedConnection = tool.Connections.Single(c => c.Id == a.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); tool.SelectedConnection = tool.Connections.Single(c => c.Id == b.Id);
            Assert.Equal(a.Id, tool.SelectedConnection!.Id);
        }
        finally { release.Set(); }
        Assert.True(await tool.SaveBeforeCloseAsync()); Assert.Contains("缺少", tool.CredentialStatus);
    }
    // 完全虚构的测试值，只写入本次临时目录，绝不读取个人密钥或 CLI 认证缓存。
    private const string TestKey = "unit-only-not-a-real-key-甲";
    private static ConnectionSettings Api(string name = "测试连接", string endpoint = "https://api.deepseek.com")
    { var preset = new ModelPreset("deepseek-flash", 8192, "high"); return new(name, ModelProvider.DeepSeek, endpoint, "", preset, preset, preset); }
    private static CredentialTarget Target(ModelConnection c) => new(c.Id, c.CredentialEpoch, c.Settings.AuthorizationEndpoint);
    [Fact]
    public async Task 同名连接独立且旧配置写入被拒绝()
    {
        await using var workspace = new TestWorkspace();
        var a = await workspace.Connections.SaveAsync(null, Api()); var b = await workspace.Connections.SaveAsync(null, Api());
        Assert.NotEqual(a.Id, b.Id);
        var updated = await workspace.Connections.SaveAsync(a, a.Settings with { Name = "改名" });
        Assert.Equal(a.Id, updated.Id); Assert.Equal(a.CredentialEpoch, updated.CredentialEpoch); Assert.Equal(a.Version + 1, updated.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Connections.SaveAsync(a, Api("过期写入")));
    }
    [Fact]
    public async Task 密钥按连接隔离且会话重建后不存在()
    {
        await using var workspace = new TestWorkspace();
        var a = await workspace.Connections.SaveAsync(null, Api()); var b = await workspace.Connections.SaveAsync(null, Api());
        await workspace.Connections.SetSecretAsync(ConnectionService.Bind(a), TestKey, false);
        Assert.Equal(CredentialState.Session, await workspace.Connections.StateAsync(ConnectionService.Bind(a)));
        Assert.Equal(CredentialState.Missing, await workspace.Connections.StateAsync(ConnectionService.Bind(b)));
        using var nextVault = new UserCredentialVault(workspace.Paths);
        Assert.Equal(CredentialState.Missing, nextVault.State(Target(a)));
        Assert.False(Directory.Exists(Path.Combine(workspace.Paths.Root, "Credentials")));
    }
    [Fact]
    public async Task 用户加密重启可读且密文与非秘密快照均不含明文()
    {
        await using var workspace = new TestWorkspace(); var c = await workspace.Connections.SaveAsync(null, Api());
        await workspace.Connections.SetSecretAsync(ConnectionService.Bind(c), TestKey, true);
        using var restarted = new UserCredentialVault(workspace.Paths);
        Assert.Equal(CredentialState.Encrypted, restarted.State(Target(c))); Assert.Equal(TestKey, restarted.ReadForRequest(Target(c)));
        var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(workspace.Paths.Root, "Credentials"))));
        Assert.DoesNotContain(TestKey, Encoding.UTF8.GetString(bytes));
        var book = BookProject.Create("独立作品") with { Connection = ConnectionService.Bind(c) };
        workspace.Store.Create(workspace.ProjectPath(), book);
        var frozen = await workspace.Connections.FreezeAsync(book, ModelTask.Drafting);
        Assert.DoesNotContain(TestKey, JsonSerializer.Serialize(book)); Assert.DoesNotContain(TestKey, JsonSerializer.Serialize(frozen));
        Assert.DoesNotContain(TestKey, Encoding.UTF8.GetString(File.ReadAllBytes(workspace.Paths.Catalog)));
    }
    [Fact]
    public async Task 端点修改再改回不能复活旧密钥且冻结配置失效()
    {
        await using var workspace = new TestWorkspace(); var a = await workspace.Connections.SaveAsync(null, Api());
        await workspace.Connections.SetSecretAsync(ConnectionService.Bind(a), TestKey, true);
        var frozen = await workspace.Connections.FreezeAsync(BookProject.Create("冻结作品") with { Connection = ConnectionService.Bind(a) }, ModelTask.Planning);
        var b = await workspace.Connections.SaveAsync(a, Api(endpoint: "https://example.org/v1"));
        Assert.NotEqual(a.CredentialEpoch, b.CredentialEpoch);
        Assert.Equal(CredentialState.Missing, await workspace.Connections.StateAsync(ConnectionService.Bind(b)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Connections.ReadSecretForRequestAsync(frozen));
        var back = await workspace.Connections.SaveAsync(b, Api());
        Assert.NotEqual(a.CredentialEpoch, back.CredentialEpoch);
        Assert.Equal(CredentialState.Missing, await workspace.Connections.StateAsync(ConnectionService.Bind(back)));
    }
    [Fact]
    public async Task 密钥清除或替换作用于后续请求而非缓存到冻结对象()
    {
        await using var workspace = new TestWorkspace(); var c = await workspace.Connections.SaveAsync(null, Api());
        var binding = ConnectionService.Bind(c);
        var frozen = await workspace.Connections.FreezeAsync(BookProject.Create("请求作品") with { Connection = binding }, ModelTask.Checking);
        await workspace.Connections.SetSecretAsync(binding, TestKey, true);
        await workspace.Connections.SetSecretAsync(binding, "unit-new-secret", false);
        Assert.Equal("unit-new-secret", await workspace.Connections.ReadSecretForRequestAsync(frozen));
        await workspace.Connections.ClearSecretAsync(binding);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Connections.ReadSecretForRequestAsync(frozen));
        using var restarted = new UserCredentialVault(workspace.Paths); Assert.Equal(CredentialState.Missing, restarted.State(Target(c)));
    }
    [Fact]
    public async Task 损坏密文显示不可用且不能取得密钥()
    {
        await using var workspace = new TestWorkspace(); var c = await workspace.Connections.SaveAsync(null, Api());
        await workspace.Connections.SetSecretAsync(ConnectionService.Bind(c), TestKey, true);
        File.WriteAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(workspace.Paths.Root, "Credentials"))), [0, 1, 2]);
        Assert.Equal(CredentialState.Unavailable, workspace.Vault.State(Target(c)));
        Assert.Throws<CryptographicException>(() => workspace.Vault.ReadForRequest(Target(c)));
    }
    [Fact]
    public async Task 新书默认不更改旧书且无密钥不阻止保存编辑()
    {
        await using var workspace = new TestWorkspace(); var a = await workspace.Connections.SaveAsync(null, Api("甲连接"));
        var b = await workspace.Connections.SaveAsync(null, Api("乙连接"));
        await workspace.Connections.SetDefaultAsync(a.Id);
        await using var document = workspace.CreateDocument(); await document.InitializeAsync(new NewDocumentActivation("新书"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath("甲书"); await document.NewProjectCommand.ExecuteAsync(null);
        await workspace.Connections.SetDefaultAsync(b.Id); document.ChapterText = "没有密钥也能编辑"; await document.SaveCommand.ExecuteAsync(null);
        Assert.Equal(a.Id, workspace.Store.Read(workspace.ProjectPath("甲书")).Project.Connection!.ConnectionId);
        Assert.Contains("缺少密钥", document.BookConnectionStatus);
        workspace.Interaction.NextPath = workspace.ProjectPath("乙书"); await document.NewProjectCommand.ExecuteAsync(null);
        Assert.Equal(b.Id, workspace.Store.Read(workspace.ProjectPath("乙书")).Project.Connection!.ConnectionId);
    }
    [Fact]
    public async Task 搬迁缺失连接不自动绑定同名连接且可手动重绑()
    {
        await using var workspace = new TestWorkspace();
        var local = await workspace.Connections.SaveAsync(null, Api("同名连接"));
        var book = BookProject.Create("搬迁作品") with { Connection = new ConnectionBinding(Guid.NewGuid(), 1, "同名连接") };
        workspace.Store.Create(workspace.ProjectPath(), book);
        await using var document = workspace.CreateDocument(); await document.InitializeAsync(new NewDocumentActivation("搬迁"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
        Assert.Contains("缺少", document.BookConnectionStatus); Assert.Null(document.SelectedBookConnection);
        document.ChapterText = "离线继续写"; await document.SaveCommand.ExecuteAsync(null);
        document.SelectedBookConnection = local; await document.BindConnectionCommand.ExecuteAsync(null);
        Assert.Equal(local.Id, workspace.Store.Read(workspace.ProjectPath()).Project.Connection!.ConnectionId);
    }
    [Theory]
    [InlineData("http://example.org")]
    [InlineData("https://user:password@example.org")]
    [InlineData("https://example.org?key=secret")]
    [InlineData("https://example.org/#key")]
    public void 凭据不能放入端点字符串(string endpoint) => Assert.Throws<InvalidDataException>(() => Api(endpoint: endpoint).Validate());
    [Fact]
    public async Task Codex配置无插件密钥且任务预设冻结为所选用途()
    {
        await using var workspace = new TestWorkspace();
        var settings = Api() with
        {
            Provider = ModelProvider.CodexCli,
            Endpoint = "",
            CodexExecutable = @"C:\unit\codex.exe",
            Planning = new ModelPreset("gpt-6-astra", 4096, "medium")
        };
        var connection = await workspace.Connections.SaveAsync(null, settings);
        Assert.Equal(CredentialState.ManagedByCodex, await workspace.Connections.StateAsync(ConnectionService.Bind(connection)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Connections.SetSecretAsync(ConnectionService.Bind(connection), TestKey, true));
        var frozen = await workspace.Connections.FreezeAsync(BookProject.Create("Codex 开发") with { Connection = ConnectionService.Bind(connection) }, ModelTask.Planning);
        Assert.Equal(settings.Planning, frozen.Preset); Assert.Equal(4096, frozen.Preset.MaxOutputTokens);
    }
    [Fact]
    public async Task 面板保存清空密钥并阻止未处理输入退出()
    {
        await using var workspace = new TestWorkspace(); await using var tool = new ModelConnectionsTool(workspace.Connections, workspace.Closing);
        await tool.InitializeAsync(); tool.Provider = ModelProvider.DeepSeek; tool.Endpoint = "https://api.deepseek.com"; tool.Name = "表单连接";
        foreach (var preset in tool.Presets) preset.Model = "deepseek-flash";
        tool.SecretInput = TestKey; await tool.SaveConfigurationCommand.ExecuteAsync(null);
        Assert.Equal(TestKey, tool.SecretInput); Assert.False(await tool.SaveBeforeCloseAsync());
        await tool.SaveSecretCommand.ExecuteAsync(null); Assert.Equal("", tool.SecretInput); Assert.Contains("会话", tool.CredentialStatus);
        Assert.True(await tool.SaveBeforeCloseAsync());
        tool.Name = "退出保存的名称"; Assert.True(await tool.SaveBeforeCloseAsync());
        Assert.Equal(tool.Name, Assert.Single((await workspace.Connections.ListAsync()).Connections).Settings.Name);
    }
    private sealed class ControlledVault(ICredentialVault inner) : ICredentialVault
    {
        public Action? BeforeSet { get; init; }
        public Action? BeforeState { get; init; }
        public CredentialState State(CredentialTarget target) { BeforeState?.Invoke(); return inner.State(target); }
        public void Set(CredentialTarget target, string secret, bool persist) { BeforeSet?.Invoke(); inner.Set(target, secret, persist); }
        public void Clear(CredentialTarget target) => inner.Clear(target);
        public string ReadForRequest(CredentialTarget target) => inner.ReadForRequest(target);
    }
}
