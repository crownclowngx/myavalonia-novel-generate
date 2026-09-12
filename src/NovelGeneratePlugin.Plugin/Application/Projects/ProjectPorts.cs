using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Projects;

public sealed record StoredProject(BookProject Project, long Version);
public sealed record RecentProject(Guid Id, string Title, string Path, DateTimeOffset OpenedAt)
{ public override string ToString() => string.IsNullOrWhiteSpace(Title) ? "未命名小说" : Title; }
public sealed record RecoverySnapshot(int FormatVersion, string OriginalPath, long ExpectedVersion, BookProject Project, DateTimeOffset SavedAt, long EditVersion = 0);
public sealed record RecoveryEntry(string Path, string Title, string Description)
{ public override string ToString() => Title + " · " + Description; }

/// <summary>
/// 应用层只依赖保存契约，不依赖 SQLite。实现必须保证 Create 不覆盖已有文件、Save 原子比较版本。
/// 同步方法由会话在线程池调用，避免 SQLite 的同步磁盘访问阻塞 Avalonia UI。
/// </summary>
public interface IProjectStore
{
    StoredProject Create(string path, BookProject project);
    StoredProject Read(string path);
    long Save(string path, BookProject project, long expectedVersion);
}
/// <summary>可重建的导航目录，不拥有作品正文；目录失败与作品保存失败分开报告。</summary>
public interface IProjectCatalog
{
    void Record(RecentProject project);
    IReadOnlyList<RecentProject> List();
}
/// <summary>独立于作品磁盘位置的恢复存储；失败稿件必须先落在这里，才允许关闭写会话。</summary>
public interface IRecoveryStore
{
    string PathFor(Guid projectId, Guid sessionId);
    void Write(string path, RecoverySnapshot snapshot);
    RecoverySnapshot Read(string path);
    IReadOnlyList<RecoveryEntry> List();
    void Delete(string path);
}
/// <summary>跨进程写入所有权；租约必须一直持有到成功保存或写出恢复副本之后。</summary>
public interface IProjectLeaseProvider
{
    IDisposable Acquire(string projectPath);
}
