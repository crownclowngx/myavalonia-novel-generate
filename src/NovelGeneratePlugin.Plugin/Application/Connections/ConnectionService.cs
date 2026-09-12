using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Connections;

public interface IConnectionStore
{
    ConnectionCatalog Read();
    ModelConnection Save(Guid id, long? expectedVersion, ConnectionSettings settings);
    void SetDefault(Guid? id);
}
public enum CredentialState { Missing, Session, Encrypted, Unavailable, ManagedByCodex }
public sealed record CredentialTarget(Guid ConnectionId, Guid Epoch, string Endpoint);
/// <summary>明文只在输入和实际请求边界短暂使用；UI 查询接口只返回状态，不提供回显密钥的能力。</summary>
public interface ICredentialVault
{
    CredentialState State(CredentialTarget target);
    void Set(CredentialTarget target, string secret, bool persist);
    void Clear(CredentialTarget target);
    string ReadForRequest(CredentialTarget target);
}
public sealed class ConnectionService(IConnectionStore store, ICredentialVault vault)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Task<ConnectionCatalog> ListAsync() => Task.Run(store.Read);
    public async Task<ModelConnection> SaveAsync(ModelConnection? previous, ConnectionSettings settings)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => store.Save(previous?.Id ?? Guid.NewGuid(), previous?.Version, settings)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public Task SetDefaultAsync(Guid? id) => Task.Run(() => store.SetDefault(id));
    public static ConnectionBinding Bind(ModelConnection connection) => new(connection.Id, connection.Version, connection.Settings.Name);
    public async Task<ConnectionBinding?> DefaultForNewBookAsync()
    {
        var catalog = await ListAsync().ConfigureAwait(false);
        var connection = catalog.Connections.SingleOrDefault(c => c.Id == catalog.DefaultConnectionId);
        return connection is null ? null : Bind(connection);
    }
    private ModelConnection RequireCurrent(ConnectionBinding binding)
    {
        var current = store.Read().Connections.SingleOrDefault(c => c.Id == binding.ConnectionId) ?? throw new InvalidOperationException("本机缺少作品绑定的连接，请重新绑定。");
        if (current.Version != binding.Version) throw new InvalidOperationException("连接配置已更新，请核对后重新绑定。");
        return current;
    }
    private static CredentialTarget Target(ModelConnection connection) => new(connection.Id, connection.CredentialEpoch, connection.Settings.AuthorizationEndpoint);
    public Task<CredentialState> StateAsync(ConnectionBinding binding) => Task.Run(() =>
    {
        var current = RequireCurrent(binding);
        return current.Settings.Provider == ModelProvider.CodexCli ? CredentialState.ManagedByCodex : vault.State(Target(current));
    });
    public async Task SetSecretAsync(ConnectionBinding binding, string secret, bool persist)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var current = RequireCurrent(binding);
                if (current.Settings.Provider != ModelProvider.DeepSeek) throw new InvalidOperationException("Codex 登录由 CLI 管理，无需在插件输入密钥。");
                vault.Set(Target(current), secret, persist);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task ClearSecretAsync(ConnectionBinding binding)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => vault.Clear(Target(RequireCurrent(binding)))).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public Task<FrozenConnection> FreezeAsync(BookProject book, ModelTask task) => Task.Run(() =>
    {
        var binding = book.Connection ?? throw new InvalidOperationException("请为本书选择模型连接。");
        var current = RequireCurrent(binding);
        return new FrozenConnection(book.Id, current, task, current.Settings.Preset(task));
    });
    /// <summary>每次发送前重新检查配置和凭据。冻结对象不携带 Key，清除/替换后不能从旧运行缓存取得认证。</summary>
    public Task<string> ReadSecretForRequestAsync(FrozenConnection frozen) => Task.Run(() =>
    {
        var current = RequireCurrent(Bind(frozen.Connection));
        if (current.Settings.Provider != ModelProvider.DeepSeek) throw new InvalidOperationException("此连接不使用 API Key。");
        return vault.ReadForRequest(Target(current));
    });
}
