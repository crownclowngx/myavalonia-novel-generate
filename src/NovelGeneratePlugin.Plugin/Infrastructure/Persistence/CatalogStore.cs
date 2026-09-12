using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Infrastructure.Persistence;
/// <summary>目录仅为导航索引；索引异常不能否定已成功保存的作品库。</summary>
public sealed class CatalogStore(WorkspacePaths paths) : IProjectCatalog
{
    private readonly object _sync = new();
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root);
        var connection = ProjectStore.Connect(paths.Catalog, SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version is not (0 or 1)) throw new NotSupportedException("目录库版本不受支持。");
            ProjectStore.Execute(connection, "CREATE TABLE IF NOT EXISTS recent (path TEXT PRIMARY KEY COLLATE NOCASE, id TEXT NOT NULL, title TEXT NOT NULL, opened_at TEXT NOT NULL); PRAGMA user_version=1;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public void Record(RecentProject project)
    {
        lock (_sync)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO recent VALUES($path,$id,$title,$time) ON CONFLICT(path) DO UPDATE SET id=excluded.id,title=excluded.title,opened_at=excluded.opened_at";
            command.Parameters.AddWithValue("$path", project.Path); command.Parameters.AddWithValue("$id", project.Id.ToString());
            command.Parameters.AddWithValue("$title", project.Title); command.Parameters.AddWithValue("$time", project.OpenedAt.ToString("O"));
            command.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<RecentProject> List()
    {
        lock (_sync)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,title,path,opened_at FROM recent ORDER BY opened_at DESC LIMIT 30";
            using var reader = command.ExecuteReader(); var result = new List<RecentProject>();
            while (reader.Read()) result.Add(new RecentProject(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
            return result;
        }
    }
}
