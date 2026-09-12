using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>候选独立于作品保存，审校失败也能重开查看。库只记录正文/报告，不记录认证信息或模型推理。</summary>
public sealed class ChapterWorkStore(WorkspacePaths paths) : IChapterWorkStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root); var connection = ProjectStore.Connect(Path.Combine(paths.Root, "chapter-work.db"), SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) is not (0 or 1)) throw new NotSupportedException("章节候选库版本不受支持。");
            command.CommandText = "CREATE TABLE IF NOT EXISTS work(id TEXT PRIMARY KEY,book TEXT NOT NULL,chapter TEXT NOT NULL,snapshot TEXT NOT NULL); CREATE INDEX IF NOT EXISTS work_chapter ON work(book,chapter); PRAGMA user_version=1;";
            command.ExecuteNonQuery(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public void Save(ChapterWork work)
    {
        work.Validate(); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO work VALUES($id,$book,$chapter,$snapshot) ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot WHERE book=excluded.book AND chapter=excluded.chapter AND json_extract(snapshot,'$.UpdateSequence') < json_extract(excluded.snapshot,'$.UpdateSequence') AND json_extract(snapshot,'$.RunId')=json_extract(excluded.snapshot,'$.RunId') AND json_extract(snapshot,'$.SourceStamp')=json_extract(excluded.snapshot,'$.SourceStamp')";
        command.Parameters.AddWithValue("$id", work.Id.ToString()); command.Parameters.AddWithValue("$book", work.BookId.ToString());
        command.Parameters.AddWithValue("$chapter", work.ChapterId.ToString()); command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(work));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("候选身份或版本已变化，不能覆盖较新结果。");
    }
    public ChapterWork? Get(Guid id)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot FROM work WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is string text ? Read(text) : null;
    }
    public IReadOnlyList<ChapterWork> Recent(Guid bookId, Guid chapterId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM work WHERE book=$book AND chapter=$chapter ORDER BY rowid DESC LIMIT 20";
        command.Parameters.AddWithValue("$book", bookId.ToString()); command.Parameters.AddWithValue("$chapter", chapterId.ToString());
        using var reader = command.ExecuteReader(); var results = new List<ChapterWork>(); while (reader.Read()) results.Add(Read(reader.GetString(0))); return results;
    }
    private static ChapterWork Read(string text) { var work = JsonSerializer.Deserialize<ChapterWork>(text) ?? throw new InvalidDataException("候选记录为空。"); work.Validate(); return work; }
}
