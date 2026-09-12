using MyAvaloniaManagement.PluginSdk;
namespace NovelGeneratePlugin.Application.Projects;

/// <summary>
/// 适配 Host 的同步 Dispose 与 SDK 异步 Shutdown。同步关闭仅启动受跟踪任务，绝不阻塞 UI 等待它自己的续体。
/// 插件 Shutdown 等全部参与者结束后才排空根会话；失败向 Host 抛出，不能假装已安全释放资源。
/// </summary>
public sealed class PluginCloseCoordinator(ProjectSessions sessions) : IPluginLifecycle
{
    private readonly object _sync = new();
    private readonly HashSet<CloseRegistration> _owners = [];
    private bool _stopping;
    public CloseRegistration Register(Func<Task> close, SynchronizationContext? context)
    {
        lock (_sync)
        {
            if (_stopping) throw new InvalidOperationException("插件正在停止，不能登记新的工作区。");
            var owner = new CloseRegistration(close, context, Release); _owners.Add(owner); return owner;
        }
    }
    private void Release(CloseRegistration owner) { lock (_sync) _owners.Remove(owner); }
    public Task InitializeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        CloseRegistration[] owners;
        lock (_sync) { _stopping = true; owners = _owners.ToArray(); }
        var tasks = owners.Select(o => o.CloseAsync()).ToArray();
        // 不因网络式取消跳过本地保存。Host 自己负责超时和未结束资源的保留。
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await sessions.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class CloseRegistration(Func<Task> close, SynchronizationContext? context, Action<CloseRegistration> release)
{
    private readonly object _sync = new();
    private Task? _completion;
    public Task CloseAsync()
    {
        TaskCompletionSource source;
        lock (_sync)
        {
            if (_completion is { IsFaulted: false, IsCanceled: false }) return _completion;
            source = new(TaskCreationOptions.RunContinuationsAsynchronously); _completion = source.Task;
            // 同步 Host 不 await 返回值；读取异常防止未观察任务，失败仍保留在登记中供 Shutdown 重试/报告。
            _ = source.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        try
        {
            if (context is not null && !ReferenceEquals(context, SynchronizationContext.Current)) context.Post(_ => Run(source), null);
            else Run(source);
        }
        catch (Exception exception) { source.TrySetException(exception); }
        return source.Task;
    }
    private async void Run(TaskCompletionSource source)
    {
        try { await close(); release(this); source.TrySetResult(); }
        catch (Exception exception) { source.TrySetException(exception); }
    }
}
