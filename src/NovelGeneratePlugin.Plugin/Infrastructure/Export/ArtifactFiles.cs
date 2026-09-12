using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Export;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
namespace NovelGeneratePlugin.Infrastructure.Export;

public sealed record TemplateTransfer(int FormatVersion, TemplateAsset Template);
/// <summary>只向新路径提交完整文件。临时文件与目标同目录，失败不修改现有输出；SQLite 用在线备份而非主文件复制。</summary>
public sealed class ArtifactFiles(IProjectStore projects, IProjectLeaseProvider leases) : IArtifactFiles
{
    private static string NewDestination(string path, string extension)
    {
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择以 " + extension + " 结尾的新文件。");
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("输出已存在，请选择新文件名；现有文件未覆盖。");
        return full;
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void WriteManuscript(string path, string content, ManuscriptFormat format)
    {
        if (!Enum.IsDefined(format)) throw new InvalidDataException("不支持此导出格式。");
        WriteNew(NewDestination(path, format == ManuscriptFormat.Text ? ".txt" : ".md"), new UTF8Encoding(false).GetBytes(content));
    }
    public void CopyProject(string sourcePath, string destinationPath)
    {
        var target = NewDestination(destinationPath, ".noveldb"); var source = Path.GetFullPath(sourcePath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份或恢复目标必须与源文件不同。");
        using var ownership = leases.Acquire(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = ProjectStore.Connect(source, SqliteOpenMode.ReadOnly))
            using (var output = ProjectStore.Connect(temporary, SqliteOpenMode.ReadWriteCreate)) input.BackupDatabase(output);
            projects.Read(temporary); // 在发布目标文件前验证身份、结构和已知版本；旧备份只迁移临时副本。
            using (var verified = ProjectStore.Connect(temporary))
            using (var checkpoint = verified.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var result = checkpoint.ExecuteReader();
                if (!result.Read() || result.GetInt32(0) != 0) throw new IOException("备份检查点尚未完成，未发布目标文件。");
            }
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(temporary, target, false);
        }
        finally
        {
            // 只清理由本次唯一临时文件产生的副文件；原始备份和已发布目标不在此清单中。
            foreach (var path in new[] { temporary, temporary + "-wal", temporary + "-shm" }) if (File.Exists(path)) File.Delete(path);
            foreach (var backup in Directory.EnumerateFiles(Path.GetDirectoryName(temporary)!, Path.GetFileName(temporary) + ".before-v*-*.noveldb")) File.Delete(backup);
        }
    }
    public TemplateAsset ReadTemplate(string path)
    {
        if (new FileInfo(path).Length > 20_000_000) throw new InvalidDataException("模板文件超过 20 MB，本阶段不导入。");
        using var stream = File.OpenRead(path);
        var package = JsonSerializer.Deserialize<TemplateTransfer>(stream) ?? throw new InvalidDataException("模板文件为空。");
        if (package.FormatVersion != 1 || package.Template is null) throw new InvalidDataException("模板交换格式不受支持。");
        package.Template.Validate(); return package.Template;
    }
    public void WriteTemplate(string path, TemplateAsset template)
    {
        template.Validate(); WriteNew(NewDestination(path, ".json"), JsonSerializer.SerializeToUtf8Bytes(new TemplateTransfer(1, template)));
    }
}
