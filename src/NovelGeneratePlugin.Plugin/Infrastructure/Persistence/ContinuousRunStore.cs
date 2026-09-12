using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Models;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

public sealed class ContinuousRunStore(WorkspacePaths paths) : IContinuousRunStore
{
    public IDisposable Acquire(Guid bookId)
    {
        if (bookId == Guid.Empty) throw new ArgumentException("缺少作品身份。");
        var directory = Path.Combine(paths.Root, "RunLocks"); Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, bookId.ToString("N") + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("本书已有连续任务正在执行，请等待完成或取消。"); }
    }
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root); var connection = ProjectStore.Connect(Path.Combine(paths.Root, "continuous-runs.db"), SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) is not (0 or 1)) throw new NotSupportedException("连续运行记录版本不受支持。");
            command.CommandText = "CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY,book TEXT NOT NULL,active INTEGER NOT NULL,sequence INTEGER NOT NULL,snapshot TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS run_active_book ON runs(book) WHERE active=1; PRAGMA user_version=1;";
            command.ExecuteNonQuery(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static int Active(ContinuousRun run) => run.State is ContinuousRunState.Completed or ContinuousRunState.Cancelled ? 0 : 1;
    public void Create(ContinuousRun run)
    {
        run.Validate(); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO runs VALUES($id,$book,$active,$sequence,$snapshot)"; Bind(command, run);
        try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidOperationException("本书已有未结束运行，请恢复或明确取消它。"); }
    }
    public void Save(ContinuousRun run)
    {
        run.Validate(); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET active=$active,sequence=$sequence,snapshot=$snapshot WHERE id=$id AND book=$book AND sequence=$sequence-1"; Bind(command, run);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("运行检查点已变化，不能覆盖其他执行结果。");
    }
    private static void Bind(SqliteCommand command, ContinuousRun run)
    {
        command.Parameters.AddWithValue("$id", run.Id.ToString()); command.Parameters.AddWithValue("$book", run.BookId.ToString()); command.Parameters.AddWithValue("$active", Active(run));
        command.Parameters.AddWithValue("$sequence", run.Sequence); command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(run));
    }
    public ContinuousRun? Get(Guid id)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot FROM runs WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is string text ? Read(text) : null;
    }
    public ContinuousRun? Latest(Guid bookId)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot FROM runs WHERE book=$book ORDER BY active DESC,rowid DESC LIMIT 1"; command.Parameters.AddWithValue("$book", bookId.ToString());
        return command.ExecuteScalar() is string text ? Read(text) : null;
    }
    private static ContinuousRun Read(string text) { var run = JsonSerializer.Deserialize<ContinuousRun>(text) ?? throw new InvalidDataException("运行记录为空。"); run.Validate(); return run; }
}
