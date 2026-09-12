using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

public sealed class MaterialStore(WorkspacePaths paths) : IMaterialStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root); var connection = ProjectStore.Connect(Path.Combine(paths.Root, "materials.db"), SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version"; if (Convert.ToInt32(command.ExecuteScalar()) is not (0 or 1)) throw new NotSupportedException("材料库版本不受支持。");
            command.CommandText = "CREATE TABLE IF NOT EXISTS materials(id TEXT PRIMARY KEY,version INTEGER NOT NULL,snapshot TEXT NOT NULL); PRAGMA user_version=1;"; command.ExecuteNonQuery(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public MaterialDocument Save(MaterialDocument material)
    {
        material.Validate(); using var connection = Open(); using var command = connection.CreateCommand(); var next = material with { Version = material.Version + 1 };
        command.CommandText = material.Version == 0 ? "INSERT INTO materials VALUES($id,$next,$snapshot)" : "UPDATE materials SET version=$next,snapshot=$snapshot WHERE id=$id AND version=$previous";
        command.Parameters.AddWithValue("$id", material.Id.ToString()); command.Parameters.AddWithValue("$next", next.Version); command.Parameters.AddWithValue("$previous", material.Version); command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(next));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("材料已由另一操作更新，请重新读取后复核。"); return next;
    }
    public MaterialDocument Read(Guid id)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot FROM materials WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        return Parse(command.ExecuteScalar() as string ?? throw new FileNotFoundException("材料记录不存在。"));
    }
    public IReadOnlyList<MaterialDocument> List()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot FROM materials ORDER BY rowid DESC LIMIT 100";
        using var reader = command.ExecuteReader(); var list = new List<MaterialDocument>(); while (reader.Read()) list.Add(Parse(reader.GetString(0))); return list;
    }
    private static MaterialDocument Parse(string text) { var result = JsonSerializer.Deserialize<MaterialDocument>(text) ?? throw new InvalidDataException("材料记录为空。"); result.Validate(); return result; }
}
