using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 来源资产的 SQLite 实现。书目、正文和章节分别保存，列表与改名不会搬运整本原文。
/// 事务保证四类记录同时可见；领域校验放在事务之前，外键和唯一键再守住持久化边界。
/// 只初始化确认为空的新库；外来数据库和未来版本在执行任何建表或业务写入前拒绝。
/// </summary>
public sealed class ReferenceSourceStore(WorkspacePaths paths) : IReferenceSourceStore
{
    public const int SchemaVersion = 1;
    private const int ApplicationId = 0x4E524546;
    public string DatabasePath => Path.Combine(paths.Root, "reference-analysis.db");

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root);
        var connection = ProjectStore.Connect(DatabasePath, SqliteOpenMode.ReadWriteCreate);
        try
        {
            // 先取得写锁再判断版本，两个服务实例同时首次使用时不会各自创建半套表。
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA application_id";
            var application = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (application == 0 && version == 0)
            {
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("分析库路径已被其他数据库占用，未修改其内容。");
                command.CommandText = $"""
                    CREATE TABLE reference_books(id TEXT PRIMARY KEY, source TEXT NOT NULL UNIQUE, version INTEGER NOT NULL, snapshot TEXT NOT NULL);
                    CREATE TABLE reference_sources(id TEXT PRIMARY KEY, book TEXT NOT NULL UNIQUE REFERENCES reference_books(id), name TEXT NOT NULL,
                        encoding TEXT NOT NULL, bytes BLOB NOT NULL, byte_hash TEXT NOT NULL, text TEXT NOT NULL, text_hash TEXT NOT NULL);
                    CREATE TABLE reference_sections(id TEXT PRIMARY KEY, book TEXT NOT NULL REFERENCES reference_books(id), number INTEGER NOT NULL,
                        snapshot TEXT NOT NULL, UNIQUE(book,number));
                    CREATE TABLE reference_chunks(id TEXT PRIMARY KEY, book TEXT NOT NULL REFERENCES reference_books(id), section TEXT NOT NULL REFERENCES reference_sections(id),
                        number INTEGER NOT NULL, snapshot TEXT NOT NULL, UNIQUE(book,number));
                    PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};
                    """;
                command.ExecuteNonQuery();
            }
            else if (application != ApplicationId) throw new InvalidDataException("文件不是参考小说分析库。");
            else if (version != SchemaVersion) throw new NotSupportedException("分析库版本不受支持，未进行迁移或写入。");
            transaction.Commit();
            ProjectStore.Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public ReferenceBook Import(ReferenceImport source)
    {
        source.Validate();
        if (source.Book.Version != 0) throw new InvalidOperationException("导入需要尚未保存的书目，不能覆盖已有来源。");
        var book = source.Book with { Version = 1 };
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO reference_books VALUES($id,$source,1,$snapshot)";
        command.Parameters.AddWithValue("$id", book.Id.ToString());
        command.Parameters.AddWithValue("$source", book.SourceId.ToString());
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(book));
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO reference_sources VALUES($id,$book,$name,$encoding,$bytes,$byteHash,$text,$textHash)";
        command.Parameters.AddWithValue("$id", source.Source.Id.ToString());
        command.Parameters.AddWithValue("$book", book.Id.ToString());
        command.Parameters.AddWithValue("$name", source.Source.FileName);
        command.Parameters.AddWithValue("$encoding", source.Source.EncodingName);
        command.Parameters.AddWithValue("$bytes", source.Source.Bytes.ToArray());
        command.Parameters.AddWithValue("$byteHash", source.Source.ByteHash);
        command.Parameters.AddWithValue("$text", source.Source.Text);
        command.Parameters.AddWithValue("$textHash", source.Source.TextHash);
        command.ExecuteNonQuery();

        foreach (var section in source.Sections)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO reference_sections VALUES($id,$book,$number,$snapshot)";
            command.Parameters.AddWithValue("$id", section.Id.ToString());
            command.Parameters.AddWithValue("$book", book.Id.ToString());
            command.Parameters.AddWithValue("$number", section.Number);
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(section));
            command.ExecuteNonQuery();
        }
        foreach (var chunk in source.Chunks)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO reference_chunks VALUES($id,$book,$section,$number,$snapshot)";
            command.Parameters.AddWithValue("$id", chunk.Id.ToString());
            command.Parameters.AddWithValue("$book", book.Id.ToString());
            command.Parameters.AddWithValue("$section", chunk.SectionId.ToString());
            command.Parameters.AddWithValue("$number", chunk.Number);
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(chunk));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return book;
    }

    public ReferenceBook ReadBook(Guid id)
    {
        using var connection = Open();
        return ReadBook(connection, id);
    }

    private static ReferenceBook ReadBook(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT snapshot,version,source FROM reference_books WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new FileNotFoundException("参考小说不存在。");
        var book = Parse<ReferenceBook>(reader.GetString(0)); book.Validate();
        if (book.Id != id || book.Version != reader.GetInt64(1) || book.SourceId.ToString() != reader.GetString(2))
            throw new InvalidDataException("参考小说书目身份或版本不一致。");
        return book;
    }

    public SourceSnapshot ReadSource(Guid bookId)
    {
        using var connection = Open();
        var book = ReadBook(connection, bookId);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,encoding,bytes,byte_hash,text,text_hash FROM reference_sources WHERE book=$book";
        command.Parameters.AddWithValue("$book", bookId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("参考小说的来源快照缺失。");
        var source = new SourceSnapshot(Guid.Parse(reader.GetString(0)), bookId, reader.GetString(1), reader.GetString(2),
            [.. (byte[])reader.GetValue(3)], reader.GetString(4), reader.GetString(5), reader.GetString(6));
        source.Validate();
        if (source.Id != book.SourceId || source.Text.Length != book.Characters) throw new InvalidDataException("书目与来源快照不一致。");
        return source;
    }

    public ReferenceImport Read(Guid bookId)
    {
        var result = new ReferenceImport(ReadBook(bookId), ReadSource(bookId),
            ReadRows<ReferenceSection>(bookId, "reference_sections"), ReadRows<AnalysisChunk>(bookId, "reference_chunks"));
        // 来源不可变、改名不改 SourceId；整份读入再验证区间，磁盘损坏不能伪装为完整覆盖。
        result.Validate();
        return result;
    }

    // 表名只由上面的封闭用例传入，调用者不能提供 SQL 标识符；数据参数始终通过参数绑定。
    private ImmutableArray<T> ReadRows<T>(Guid bookId, string table)
    {
        using var connection = Open(); ReadBook(connection, bookId);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT snapshot FROM {table} WHERE book=$book ORDER BY number";
        command.Parameters.AddWithValue("$book", bookId.ToString());
        using var reader = command.ExecuteReader(); var result = ImmutableArray.CreateBuilder<T>();
        while (reader.Read()) result.Add(Parse<T>(reader.GetString(0)));
        return result.ToImmutable();
    }

    public IReadOnlyList<ReferenceBook> List(int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM reference_books ORDER BY rowid DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", limit); command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader(); var result = new List<ReferenceBook>();
        while (reader.Read()) { var book = Parse<ReferenceBook>(reader.GetString(0)); book.Validate(); result.Add(book); }
        return result;
    }

    public ReferenceBook Rename(Guid id, long expectedVersion, string name)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var previous = ReadBook(connection, id, transaction);
        if (previous.Version != expectedVersion) throw new InvalidOperationException("书目已由另一操作更新，请重新读取后修改。");
        var next = previous with { Name = name, Version = checked(previous.Version + 1) }; next.Validate();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE reference_books SET snapshot=$snapshot,version=$next WHERE id=$id AND version=$previous";
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(next)); command.Parameters.AddWithValue("$next", next.Version);
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$previous", expectedVersion);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("书目版本冲突，改名未保存。");
        transaction.Commit(); return next;
    }

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("分析记录为空或损坏。");
}
