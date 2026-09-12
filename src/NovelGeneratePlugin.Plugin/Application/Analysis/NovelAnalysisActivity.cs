using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record AnalysisActivityUpdate(AnalysisRun Run, IReadOnlyList<ModelRequestEntry> Usage, bool Active, string? Error = null);

/// <summary>
/// 根级服务拥有长任务，View 和面板只订阅快照。隐藏工具、切换书目或关闭创作 Document 都不会释放此服务。
/// 同一运行只启动一次；最终退出先取消在途请求，再等待账本和检查点收尾，不能以窗口消失代替任务完成。
/// </summary>
public sealed class NovelAnalysisActivity(NovelAnalysisRunService runner, PluginCloseCoordinator shutdown) : IAsyncDisposable, IDisposable
{
    private sealed class Work
    {
        public AnalysisRunControl Control { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<AnalysisRun> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Work> _active = [];
    private CloseRegistration? _registration;
    private bool _stopping;
    public event Action<AnalysisActivityUpdate>? Changed;
    public bool IsActive(Guid id) { lock (_sync) return _active.ContainsKey(id); }
    public Task<AnalysisRun> StartAsync(Guid id)
    {
        lock (_sync)
        {
            if (_stopping) throw new InvalidOperationException("插件正在退出，不能启动分析。");
            if (_active.TryGetValue(id, out var existing)) return existing.Completion.Task;
            _registration ??= shutdown.Register(StopAsync, null);
            var work = new Work(); _active.Add(id, work);
            _ = ExecuteAsync(id, work); return work.Completion.Task;
        }
    }
    public void Pause(Guid id) { lock (_sync) if (_active.TryGetValue(id, out var work)) work.Control.Pause(); }
    public void Cancel(Guid id) { lock (_sync) if (_active.TryGetValue(id, out var work)) work.Cancellation.Cancel(); }
    private async Task ExecuteAsync(Guid id, Work work)
    {
        AnalysisRun? result = null; Exception? failure = null;
        try
        {
            result = await runner.ExecuteAsync(id, work.Control, new ActivityProgress(run => Publish(run, true)), work.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_sync) { _active.Remove(id); work.Cancellation.Dispose(); }
            try { Publish(result ?? runner.Read(id), false, failure?.Message); }
            catch (Exception error) { failure ??= error; }
            if (failure is null) work.Completion.TrySetResult(result!); else work.Completion.TrySetException(failure);
        }
    }
    private void Publish(AnalysisRun run, bool active, string? error = null)
    {
        // 读取用量发生在调度线程。UI 收到的是已构造快照，不在每次绑定或绘制时访问 SQLite。
        var update = new AnalysisActivityUpdate(run, runner.Usage(run.Id), active, error);
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action<AnalysisActivityUpdate>)handler)(update); }
            catch (Exception) { /* 展示订阅者失效不能中断已付费结果提交；重新打开时仍从持久化运行读取。 */ }
    }
    public async Task StopAsync()
    {
        lock (_sync) _stopping = true;
        await DrainAsync().ConfigureAwait(false);
    }
    /// <summary>关闭预检查可以因其他草案失败而撤销，因此排空在途任务与永久停止服务分别处理。</summary>
    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            foreach (var work in _active.Values) work.Cancellation.Cancel();
            pending = _active.Values.Select(work => work.Completion.Task).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => new(_registration?.CloseAsync() ?? StopAsync());
    // Host 的 Provider 使用同步 Dispose；仅触发已登记的排空任务，Shutdown 负责等待，不能阻塞 UI 线程。
    public void Dispose() => _ = _registration?.CloseAsync() ?? StopAsync();
    private sealed class ActivityProgress(Action<AnalysisRun> report) : IProgress<AnalysisRun> { public void Report(AnalysisRun value) => report(value); }
}
