using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>目录只保存非秘密连接配置。新书默认项是独立设置，不反向修改已经绑定的作品。</summary>
public sealed class ConnectionStore(WorkspacePaths paths) : IConnectionStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root); var connection = ProjectStore.Connect(paths.Catalog, SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var transaction = connection.BeginTransaction(); CheckSchema(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "CREATE TABLE IF NOT EXISTS model_connections(id TEXT PRIMARY KEY, version INTEGER NOT NULL, snapshot TEXT NOT NULL); CREATE TABLE IF NOT EXISTS connection_preferences(singleton INTEGER PRIMARY KEY CHECK(singleton=1), default_id TEXT); PRAGMA user_version=1;";
            command.ExecuteNonQuery(); transaction.Commit(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static void CheckSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(command.ExecuteScalar()) is not (0 or 1)) throw new NotSupportedException("目录库格式不受支持，未写入连接。");
    }
    private static ModelConnection Parse(SqliteDataReader reader)
    {
        var connection = JsonSerializer.Deserialize<ModelConnection>(reader.GetString(2)) ?? throw new InvalidDataException("连接内容为空。");
        connection.Validate();
        if (connection.Id.ToString() != reader.GetString(0) || connection.Version != reader.GetInt64(1)) throw new InvalidDataException("连接数据列与快照不一致。");
        return connection;
    }
    public ConnectionCatalog Read()
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); CheckSchema(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT id,version,snapshot FROM model_connections";
        var entries = new List<ModelConnection>(); using (var reader = command.ExecuteReader()) while (reader.Read()) entries.Add(Parse(reader));
        command.CommandText = "SELECT default_id FROM connection_preferences WHERE singleton=1";
        var value = command.ExecuteScalar() as string;
        var defaultId = value is null ? (Guid?)null : Guid.TryParse(value, out var id) ? id : throw new InvalidDataException("默认连接身份无效。");
        transaction.Commit(); return new ConnectionCatalog(entries.OrderBy(c => c.Settings.Name).ToArray(), defaultId);
    }
    public ModelConnection Save(Guid id, long? expectedVersion, ConnectionSettings settings)
    {
        settings.Validate(); if (id == Guid.Empty) throw new InvalidDataException("连接身份无效。");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); CheckSchema(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var epoch = Guid.NewGuid();
        if (expectedVersion is long version)
        {
            command.CommandText = "SELECT id,version,snapshot FROM model_connections WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("连接已不存在。");
            var old = Parse(reader);
            if (old.Version != version) throw new InvalidOperationException("连接已被其他编辑更新，请重新读取。");
            if (old.Settings.Provider == settings.Provider && old.Settings.AuthorizationEndpoint == settings.AuthorizationEndpoint) epoch = old.CredentialEpoch;
        }
        var saved = new ModelConnection(id, checked((expectedVersion ?? 0) + 1), epoch, settings); saved.Validate();
        command.Parameters.Clear();
        command.CommandText = expectedVersion is null ? "INSERT INTO model_connections VALUES($id,$version,$snapshot)" : "UPDATE model_connections SET version=$version,snapshot=$snapshot WHERE id=$id AND version=$expected";
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$version", saved.Version); command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(saved));
        if (expectedVersion is long expected) command.Parameters.AddWithValue("$expected", expected);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("连接版本冲突，未覆盖。");
        transaction.Commit(); return saved;
    }
    public void SetDefault(Guid? id)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); CheckSchema(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        if (id is Guid selected)
        {
            command.CommandText = "SELECT COUNT(*) FROM model_connections WHERE id=$id"; command.Parameters.AddWithValue("$id", selected.ToString());
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("默认连接不存在。");
        }
        command.Parameters.Clear(); command.CommandText = "INSERT INTO connection_preferences VALUES(1,$id) ON CONFLICT(singleton) DO UPDATE SET default_id=$id";
        command.Parameters.AddWithValue("$id", id is null ? DBNull.Value : id.Value.ToString()); command.ExecuteNonQuery(); transaction.Commit();
    }
}
