using System.Text.Json;
using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Infrastructure.Persistence;
/// <summary>恢复副本采用先写临时文件、强制刷新再原子替换，避免半个 JSON 被当作可恢复内容。</summary>
public sealed class RecoveryStore(WorkspacePaths paths) : IRecoveryStore
{
    public string PathFor(Guid projectId, Guid sessionId) => System.IO.Path.Combine(paths.RecoveryDirectory, $"{projectId:N}-{sessionId:N}.json");
    public void Write(string path, RecoverySnapshot snapshot)
    {
        Directory.CreateDirectory(paths.RecoveryDirectory);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, snapshot); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { /* 从未发生失败时，恢复目录尚不存在；这不是清理故障。 */ }
    }
    public RecoverySnapshot Read(string path)
    {
        var root = System.IO.Path.GetFullPath(paths.RecoveryDirectory) + System.IO.Path.DirectorySeparatorChar;
        if (!System.IO.Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("恢复文件不在恢复目录内。");
        using var stream = File.OpenRead(path);
        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(stream) ?? throw new InvalidDataException("恢复文件内容为空。");
        if (snapshot.FormatVersion is not (1 or 2 or 3 or 4 or 5)) throw new NotSupportedException("恢复文件版本不受支持。");
        snapshot.Project.Validate(); return snapshot;
    }
    public IReadOnlyList<RecoveryEntry> List()
    {
        if (!Directory.Exists(paths.RecoveryDirectory)) return [];
        var entries = new List<RecoveryEntry>();
        foreach (var file in Directory.EnumerateFiles(paths.RecoveryDirectory, "*.json"))
        {
            try { var data = Read(file); entries.Add(new RecoveryEntry(file, data.Project.Title, data.SavedAt.ToLocalTime().ToString("g"))); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
            { entries.Add(new RecoveryEntry(file, "无法读取的恢复副本", "保留原文件，请检查格式")); }
        }
        return entries;
    }
}
