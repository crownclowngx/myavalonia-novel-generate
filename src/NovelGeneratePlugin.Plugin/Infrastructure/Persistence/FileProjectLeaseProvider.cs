using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 使用系统文件共享锁。进程退出由操作系统释放锁；遗留的空锁文件不代表作品仍被占用。
/// 不删除锁文件，避免一个持有者释放时误删另一个新持有者已经打开的路径。
/// </summary>
public sealed class FileProjectLeaseProvider : IProjectLeaseProvider
{
    public IDisposable Acquire(string projectPath)
    {
        try { return new FileStream(projectPath + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new IOException("项目正在其他窗口或进程中使用，或该目录不可写。", exception); }
    }
}
