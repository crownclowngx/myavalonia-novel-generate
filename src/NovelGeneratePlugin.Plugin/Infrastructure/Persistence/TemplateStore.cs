using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>模板在共享目录库中独立存储；旧版最近项目操作不更新或删除模板表。</summary>
public sealed class TemplateStore(WorkspacePaths paths) : ITemplateStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root);
        var connection = ProjectStore.Connect(paths.Catalog, SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is not (0 or 1)) throw new NotSupportedException("目录库版本不受支持。");
            command.CommandText = "CREATE TABLE IF NOT EXISTS templates (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, snapshot TEXT NOT NULL); PRAGMA user_version=1;";
            command.ExecuteNonQuery(); transaction.Commit(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public IReadOnlyList<TemplateAsset> List()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT id,revision,snapshot FROM templates";
        using var reader = command.ExecuteReader(); var result = new List<TemplateAsset>();
        while (reader.Read()) result.Add(Parse(reader));
        return result.OrderBy(a => a.Draft.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public TemplateAsset Read(Guid id)
    {
        using var connection = Open(); return Read(connection, id);
    }
    private static TemplateAsset Read(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,revision,snapshot FROM templates WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Parse(reader) : throw new InvalidOperationException("模板不存在，可能已被外部移除。");
    }
    private static TemplateAsset Parse(SqliteDataReader reader)
    {
        var result = JsonSerializer.Deserialize<TemplateAsset>(reader.GetString(2)) ?? throw new InvalidDataException("模板内容为空。");
        result.Validate();
        // 数据列用于并发比较，快照用于还原内容；两者必须指向同一实体，损坏时禁止继续覆盖。
        if (!Guid.TryParse(reader.GetString(0), out var id) || id != result.Id || reader.GetInt64(1) != result.Revision)
            throw new InvalidDataException("模板数据列与内容快照的身份或版本不一致。");
        return result;
    }
    public TemplateAsset Save(TemplateAsset asset, long? expectedRevision)
    {
        asset.Validate(); using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction; schema.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(schema.ExecuteScalar()) != 1) throw new NotSupportedException("目录库格式已变化，未执行模板写入。");
        }
        if (expectedRevision is long previousRevision)
        {
            var previous = Read(connection, asset.Id, transaction);
            if (previous.Revision != previousRevision || asset.Versions.Length < previous.Versions.Length ||
                !asset.Versions.Take(previous.Versions.Length).SequenceEqual(previous.Versions))
                throw new InvalidOperationException("模板已变化，或试图覆盖已保存版本。请重新读取模板。");
        }
        var saved = asset with { Revision = expectedRevision is long revision ? checked(revision + 1) : 0 };
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = expectedRevision is null ? "INSERT INTO templates VALUES($id,$revision,$snapshot)" :
            "UPDATE templates SET revision=$revision,snapshot=$snapshot WHERE id=$id AND revision=$expected";
        command.Parameters.AddWithValue("$id", saved.Id.ToString()); command.Parameters.AddWithValue("$revision", saved.Revision);
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(saved));
        if (expectedRevision is long expected) command.Parameters.AddWithValue("$expected", expected);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("模板版本冲突，未覆盖现有内容。");
        transaction.Commit(); return saved;
    }
    private string RecoveryPath(Guid id) => Path.Combine(paths.Root, "TemplateRecovery", id.ToString("N") + ".json");
    public void WriteRecovery(TemplateDraftRecovery recovery)
    {
        recovery.Asset.Validate();
        var path = RecoveryPath(recovery.Id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, recovery); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public TemplateDraftRecovery ReadRecovery(Guid id)
    {
        using var stream = File.OpenRead(RecoveryPath(id));
        var recovery = JsonSerializer.Deserialize<TemplateDraftRecovery>(stream) ?? throw new InvalidDataException("模板恢复草案为空。");
        if (recovery.FormatVersion != 1 || recovery.Id != id || recovery.EditGeneration < 0 || recovery.Asset is null) throw new InvalidDataException("模板恢复格式无效。");
        recovery.Asset.Validate(); return recovery;
    }
    public IReadOnlyList<TemplateRecoveryEntry> ListRecovery()
    {
        var directory = Path.Combine(paths.Root, "TemplateRecovery");
        if (!Directory.Exists(directory)) return [];
        var result = new List<TemplateRecoveryEntry>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            try { var saved = ReadRecovery(id); result.Add(new TemplateRecoveryEntry(id, saved.Asset.Draft.Name, true)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { result.Add(new TemplateRecoveryEntry(id, "损坏或不可访问的恢复草案", false)); }
        }
        return result;
    }
    public void DeleteRecovery(Guid id)
    { try { File.Delete(RecoveryPath(id)); } catch (DirectoryNotFoundException) { } }

}
