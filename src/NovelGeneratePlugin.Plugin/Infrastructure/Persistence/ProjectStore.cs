using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Infrastructure.Persistence;


/// <summary>作品聚合在一笔事务中替换；版本比较阻止外部变化被旧快照覆盖。</summary>
public sealed class ProjectStore : IProjectStore
{
    public const int SchemaVersion = 5;
    private const int ApplicationId = 0x4E4F564C;
    public static SqliteConnection Connect(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 2 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
    public StoredProject Create(string path, BookProject project)
    {
        project.Validate();
        // CreateNew 是覆盖保护；选择器中的覆盖确认也不能把已有小说库抹掉。
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using var connection = Connect(path);
            Execute(connection, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion}; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "CREATE TABLE project (singleton INTEGER PRIMARY KEY CHECK(singleton=1), id TEXT NOT NULL, version INTEGER NOT NULL, snapshot TEXT NOT NULL); INSERT INTO project VALUES(1,$id,0,$snapshot);";
            command.Parameters.AddWithValue("$id", project.Id.ToString());
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(project));
            command.ExecuteNonQuery(); transaction.Commit();
            return new StoredProject(project, 0);
        }
        catch
        {
            // 仅删除本次 CreateNew 创建的失败文件，不处理用户已有项目。
            File.Delete(path);
            throw;
        }
    }
    public StoredProject Read(string path)
    {
        using (var connection = Connect(path, SqliteOpenMode.ReadOnly))
        {
            var version = ReadSchema(connection);
            if (version == SchemaVersion) return ReadSnapshot(connection);
            if (version is not (1 or 2 or 3 or 4)) throw new NotSupportedException("项目格式版本不受支持，未进行迁移或写入。");
            ReadSnapshot(connection); // 先验证旧数据；损坏项目不能被包装为成功迁移。
        }
        MigrateKnownFormat(path);
        using var migrated = Connect(path, SqliteOpenMode.ReadOnly);
        ValidateSchema(migrated); return ReadSnapshot(migrated);
    }
    private static StoredProject ReadSnapshot(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, version, snapshot FROM project WHERE singleton=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("项目中没有作品记录。");
        var project = JsonSerializer.Deserialize<BookProject>(reader.GetString(2)) ?? throw new InvalidDataException("项目内容为空。");
        project.Validate();
        if (project.Id.ToString() != reader.GetString(0) || reader.GetInt64(1) < 0) throw new InvalidDataException("项目身份或版本不一致。");
        return new StoredProject(project, reader.GetInt64(1));
    }
    private static void MigrateKnownFormat(string path)
    {
        using var source = Connect(path);
        var expectedSchema = ReadSchema(source);
        if (expectedSchema is not (1 or 2 or 3 or 4)) throw new InvalidOperationException("迁移前项目版本已变化，请重新打开。");
        var backupPath = path + ".before-v5-" + Guid.NewGuid().ToString("N") + ".noveldb";
        // SQLite 在线备份包含已提交的 WAL，不能只复制活动主文件。
        using (var backup = Connect(backupPath, SqliteOpenMode.ReadWriteCreate)) source.BackupDatabase(backup);
        using var transaction = source.BeginTransaction();
        if (ReadSchema(source, transaction) != expectedSchema) throw new InvalidOperationException("事务开始时项目格式已变化，迁移已中止。");
        var original = ReadSnapshot(source, transaction);
        using var command = source.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE project SET snapshot=$snapshot, version=version+1 WHERE singleton=1; PRAGMA user_version=5;";
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(original.Project));
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public long Save(string path, BookProject project, long expectedVersion)
    {
        project.Validate();
        using var connection = Connect(path);
        Execute(connection, "PRAGMA synchronous=FULL;");
        using var transaction = connection.BeginTransaction();
        ValidateSchema(connection, transaction);
        var previous = ReadSnapshot(connection, transaction);
        project.Revisions.EnsureAppendOnlyFrom(previous.Project.Revisions);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE project SET snapshot=$snapshot, version=version+1 WHERE singleton=1 AND id=$id AND version=$version";
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(project));
        command.Parameters.AddWithValue("$id", project.Id.ToString()); command.Parameters.AddWithValue("$version", expectedVersion);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("项目已被其他写入修改，请将恢复副本另存为新项目后核对。");
        transaction.Commit();
        return expectedVersion + 1;
    }
    private static void ValidateSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        if (ReadSchema(connection, transaction) != SchemaVersion) throw new NotSupportedException("项目格式版本不受支持，未进行迁移或写入。");
    }
    private static int ReadSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt32(command.ExecuteScalar()) != ApplicationId) throw new InvalidDataException("这不是本插件的小说项目。");
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    internal static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
