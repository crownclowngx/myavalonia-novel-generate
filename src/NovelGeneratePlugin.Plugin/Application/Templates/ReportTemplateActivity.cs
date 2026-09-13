using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;

namespace NovelGeneratePlugin.Application.Templates;

public sealed record TemplateConversionUpdate(ReportTemplateConversion Conversion, IReadOnlyList<ModelRequestEntry> Usage, bool Active, string? Error = null);

/// <summary>根级服务拥有转换与本地交付。一个任务只有一个在途执行，隐藏页面只影响展示，退出先取消请求并等待必要保存。</summary>
public sealed class ReportTemplateActivity(ReportTemplateConversionService service, ReportTemplateDeliveryService delivery, PluginCloseCoordinator shutdown) : IAsyncDisposable, IDisposable
{
    private sealed class Work
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<ReportTemplateConversion> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Work> _active = [];
    private CloseRegistration? _registration;
    private bool _stopping;
    public event Action<TemplateConversionUpdate>? Changed;
    public bool IsActive(Guid id) { lock (_sync) return _active.ContainsKey(id); }
    public Task<ReportTemplateConversion> StartAsync(Guid id, bool acknowledge = false, int? maximumRequests = null, long? maximumTokens = null)
    {
        lock (_sync)
        {
            if (_stopping) throw new InvalidOperationException("插件正在退出，不能启动转换。");
            if (_active.TryGetValue(id, out var existing)) return existing.Completion.Task;
            _registration ??= shutdown.Register(StopAsync, null);
            var work = new Work(); _active.Add(id, work);
            _ = ExecuteAsync(id, work, acknowledge, maximumRequests, maximumTokens); return work.Completion.Task;
        }
    }
    public void Cancel(Guid id) { lock (_sync) if (_active.TryGetValue(id, out var work)) work.Cancellation.Cancel(); }
    private async Task ExecuteAsync(Guid id, Work work, bool acknowledge, int? maximumRequests, long? maximumTokens)
    {
        ReportTemplateConversion? result = null; Exception? failure = null;
        try
        {
            result = await service.ExecuteAsync(id, new ProgressSink(run => Publish(run, true)), work.Cancellation.Token, acknowledge, maximumRequests, maximumTokens).ConfigureAwait(false);
            if (result.State == TemplateConversionState.CandidateSaved) result = await delivery.DeliverAsync(id).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_sync) { _active.Remove(id); work.Cancellation.Dispose(); }
            try { Publish(result ?? service.Read(id), false, failure?.Message); } catch (Exception error) { failure ??= error; }
            if (failure is null) work.Completion.TrySetResult(result!); else work.Completion.TrySetException(failure);
        }
    }
    private void Publish(ReportTemplateConversion run, bool active, string? error = null)
    {
        var update = new TemplateConversionUpdate(run, service.Usage(run.Id), active, error);
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action<TemplateConversionUpdate>)handler)(update); } catch (Exception) { /* 展示失败不影响保存结果。 */ }
    }
    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_sync) { foreach (var work in _active.Values) work.Cancellation.Cancel(); pending = _active.Values.Select(w => w.Completion.Task).ToArray(); }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    public async Task StopAsync() { lock (_sync) _stopping = true; await DrainAsync().ConfigureAwait(false); }
    public ValueTask DisposeAsync() => new(_registration?.CloseAsync() ?? StopAsync());
    public void Dispose() => _ = _registration?.CloseAsync() ?? StopAsync();
    private sealed class ProgressSink(Action<ReportTemplateConversion> action) : IProgress<ReportTemplateConversion>
    { public void Report(ReportTemplateConversion value) => action(value); }
}
