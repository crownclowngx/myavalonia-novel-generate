using System.Threading.Channels;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Projects;

public enum SaveState { Saved, Unsaved, Saving, Failed }
public sealed record SaveStatus(SaveState State, string Message, bool RecoveryAvailable = false);
public sealed record SaveOutcome(bool Saved, bool RecoveryAvailable, string Message);

/// <summary>一本书一个写会话。队列合并按键；显式保存和关闭复用同一串行入口。</summary>
public sealed class ProjectSession : IAsyncDisposable
{
    private readonly IProjectStore _store;
    private readonly IRecoveryStore _recovery;
    private readonly IDisposable _lease;
    private readonly Action<ProjectSession> _released;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly SemaphoreSlim _closeGate = new(1);
    private SaveOutcome? _preparedClose;
    private long _recoveredEditVersion = -1;
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stopDelay = new();
    private readonly Task _worker;
    private BookProject _current;
    private long _databaseVersion, _editVersion, _savedEditVersion;
    private bool _closing, _disposed;
    private Task? _disposeTask;
    private SaveStatus _status = new(SaveState.Saved, "已保存");
    public string Path { get; }
    public string RecoveryPath { get; }
    public string? CatalogWarning { get; internal set; }
    public Guid Id => _current.Id;
    public BookProject Current { get { lock (_sync) return _current; } }
    public SaveStatus Status { get { lock (_sync) return _status; } }
    public event EventHandler? StateChanged;
    internal ProjectSession(string path, StoredProject stored, IDisposable lease, IProjectStore store, IRecoveryStore recovery, Action<ProjectSession> released)
    {
        Path = path; _current = stored.Project; _databaseVersion = stored.Version; _lease = lease;
        _store = store; _recovery = recovery; _released = released;
        RecoveryPath = recovery.PathFor(Id, Guid.NewGuid());
        _worker = AutoSaveAsync();
    }
    public void Update(BookProject project)
    {
        project.Validate();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing || _disposed, this);
            if (project.Id != _current.Id) throw new InvalidOperationException("不能把其他作品写入当前会话。");
            if (project == _current) return;
            _current = project; _editVersion++;
            _status = new SaveStatus(SaveState.Unsaved, "有未保存修改");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        _requests.Writer.TryWrite(true);
    }
    private async Task AutoSaveAsync()
    {
        await foreach (var _ in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await Task.Delay(450, _stopDelay.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stopDelay.IsCancellationRequested) { }
            while (_requests.Reader.TryRead(out var _)) { }
            await SaveAsync().ConfigureAwait(false);
        }
    }
    public async Task<SaveOutcome> SaveAsync()
    {
        // 不接收网络/窗口取消令牌：已经接受的编辑必须落盘或进入恢复文件。
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            BookProject snapshot; long editVersion, databaseVersion;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_preparedClose is not null) return _preparedClose;
                if (_editVersion == _savedEditVersion) return new SaveOutcome(true, false, _status.Message);
                snapshot = _current; editVersion = _editVersion; databaseVersion = _databaseVersion;
                _status = new SaveStatus(SaveState.Saving, "正在保存…");
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                var next = await Task.Run(() => _store.Save(Path, snapshot, databaseVersion)).ConfigureAwait(false);
                string? cleanupWarning = null;
                try { _recovery.Delete(RecoveryPath); }
                catch (IOException) { cleanupWarning = "；旧恢复副本未能清理"; }
                catch (UnauthorizedAccessException) { cleanupWarning = "；旧恢复副本未能清理"; }
                lock (_sync)
                {
                    _databaseVersion = next; _savedEditVersion = editVersion; _recoveredEditVersion = -1;
                    _status = _editVersion == editVersion
                        ? new SaveStatus(SaveState.Saved, "已保存 " + DateTime.Now.ToString("HH:mm:ss") + cleanupWarning)
                        : new SaveStatus(SaveState.Unsaved, "有更新的修改等待保存");
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new SaveOutcome(true, false, Status.Message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var recovered = false; var message = "保存失败：" + exception.Message;
                try
                {
                    await Task.Run(() => _recovery.Write(RecoveryPath, new RecoverySnapshot(1, Path, databaseVersion, snapshot, DateTimeOffset.UtcNow, editVersion))).ConfigureAwait(false);
                    lock (_sync) _recoveredEditVersion = editVersion;
                    message += "；本次编辑快照已写入恢复副本。";
                }
                catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
                { message += "；本次恢复副本写入失败：" + recoveryError.Message; }
                // 恢复保护必须对应当前编辑版本。较早快照的成功不能冒充新输入也已落盘。
                long currentEdit; lock (_sync) currentEdit = _editVersion;
                if (_recoveredEditVersion == currentEdit)
                {
                    try
                    {
                        var existing = await Task.Run(() => _recovery.Read(RecoveryPath)).ConfigureAwait(false);
                        recovered = existing.EditVersion == currentEdit && existing.Project.Id == Id;
                    }
                    catch (Exception verifyError) when (verifyError is not OutOfMemoryException) { recovered = false; }
                }
                lock (_sync)
                {
                    recovered = recovered && _editVersion == currentEdit;
                    message += recovered ? "；当前全部修改有恢复副本，可恢复为新项目。" : "；当前全部修改尚未获得恢复保护，请保持窗口打开并重试保存。";
                    _status = new SaveStatus(SaveState.Failed, message, recovered);
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new SaveOutcome(false, recovered, message);
            }
        }
        finally { _writeGate.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (_disposeTask is { IsFaulted: true }) _disposeTask = null;
            return new ValueTask(_disposeTask ??= CloseCoreAsync());
        }
    }
    /// <summary>
    /// 关闭采用“冻结编辑并验证落盘 → 最终释放”两步。准备失败时恢复编辑入口，队列仍然存活。
    /// 成功后缓存该编辑版本的保存证明，释放时不重复写入，避免第二次磁盘故障把安全关闭变成失效窗口。
    /// </summary>
    public async Task<SaveOutcome> PrepareCloseAsync()
    {
        await _closeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_preparedClose is not null) return _preparedClose;
                ObjectDisposedException.ThrowIf(_disposed, this);
                _closing = true;
            }
            SaveOutcome result;
            try { result = await SaveAsync().ConfigureAwait(false); }
            catch { lock (_sync) _closing = false; throw; }
            lock (_sync)
            {
                if (result.Saved || result.RecoveryAvailable) _preparedClose = result;
                else _closing = false;
            }
            return result;
        }
        finally { _closeGate.Release(); }
    }
    private async Task CloseCoreAsync()
    {
        var result = await PrepareCloseAsync().ConfigureAwait(false);
        if (!result.Saved && !result.RecoveryAvailable) throw new IOException(result.Message);
        _requests.Writer.TryComplete(); _stopDelay.Cancel();
        await _worker.ConfigureAwait(false);
        lock (_sync) _disposed = true;
        _lease.Dispose(); _released(this); _stopDelay.Dispose();
    }
}
