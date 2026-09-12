using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Projects;

/// <summary>私有根服务只拥有会话及租约，不引用 Document 或 View。</summary>
public sealed class ProjectSessions(IProjectStore store, IProjectCatalog catalog, IRecoveryStore recovery, IProjectLeaseProvider leases) : IAsyncDisposable
{
    private readonly SemaphoreSlim _openGate = new(1);
    private readonly Dictionary<Guid, ProjectSession> _sessions = [];
    private bool _disposed;
    public Task<ProjectSession> CreateAsync(string path, BookProject project, CancellationToken cancellationToken = default)
        => AcquireAsync(path, project, cancellationToken);
    public Task<ProjectSession> OpenAsync(string path, CancellationToken cancellationToken = default)
        => AcquireAsync(path, null, cancellationToken);
    private async Task<ProjectSession> AcquireAsync(string path, BookProject? project, CancellationToken cancellationToken)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetExtension(path), ".noveldb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 .noveldb 小说项目文件。");
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_sessions)
                if (_sessions.Values.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("本项目已在另一工作区打开，请切换到已有工作区。");
            var lease = leases.Acquire(path);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stored = await Task.Run(() => project is null ? store.Read(path) : store.Create(path, project), cancellationToken).ConfigureAwait(false);
                lock (_sessions)
                    if (_sessions.ContainsKey(stored.Project.Id)) throw new InvalidOperationException("同一作品的另一个副本正在编辑，不能同时写入。请使用恢复为新项目以生成独立身份。");
                // 创建若已提交，即使迟到取消也保留文件；仅不把会话交给已关闭窗口。
                cancellationToken.ThrowIfCancellationRequested();
                var session = new ProjectSession(path, stored, lease, store, recovery, Release);
                lock (_sessions) _sessions.Add(stored.Project.Id, session);
                try { await Task.Run(() => catalog.Record(new RecentProject(stored.Project.Id, stored.Project.Title, path, DateTimeOffset.UtcNow))).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { session.CatalogWarning = "作品可用，但最近项目索引更新失败：" + exception.Message; }
                return session;
            }
            catch { lease.Dispose(); throw; }
        }
        finally { _openGate.Release(); }
    }
    private void Release(ProjectSession session) { lock (_sessions) _sessions.Remove(session.Id); }
    public async ValueTask DisposeAsync()
    {
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            ProjectSession[] sessions; lock (_sessions) sessions = _sessions.Values.ToArray();
            List<Exception> failures = [];
            foreach (var session in sessions)
                try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
            if (failures.Count != 0) throw new AggregateException("部分作品未能保存或建立恢复副本。", failures);
        }
        finally { _openGate.Release(); }
    }
}
