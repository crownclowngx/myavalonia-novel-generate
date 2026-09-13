using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 运行库独立于原文库及请求账本：前者不可变，后者先记成本。运行与结果使用一个事务，跨库窗口由操作 ID 重放补偿。
/// 不使用分布式事务或通用工作流框架；文件锁保证同一运行仅一个调度者，崩溃时由操作系统自动释放。
/// </summary>
public sealed class AnalysisRunStore(WorkspacePaths paths) : IAnalysisRunStore
{
    private const int ApplicationId = 0x4E52554E;
    public string DatabasePath => Path.Combine(paths.Root, "reference-runs.db");
    public IDisposable Acquire(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("运行身份为空。", nameof(runId));
        Directory.CreateDirectory(paths.Root);
        return new FileStream(Path.Combine(paths.Root, "analysis-" + runId.ToString("N") + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root);
        var connection = ProjectStore.Connect(DatabasePath, SqliteOpenMode.ReadWriteCreate);
        try
        {
            BackupLegacy(connection);
            using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "PRAGMA application_id"; var application = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(command.ExecuteScalar());
            if (application == 0 && version == 0)
            {
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("运行库位置已被其他数据库占用。");
                command.CommandText = $"""
                    CREATE TABLE runs(id TEXT PRIMARY KEY,book TEXT NOT NULL,version INTEGER NOT NULL,snapshot TEXT NOT NULL);
                    CREATE TABLE results(run TEXT NOT NULL REFERENCES runs(id),key TEXT NOT NULL,stamp TEXT NOT NULL,json TEXT NOT NULL,hash TEXT NOT NULL,PRIMARY KEY(run,key));
                    CREATE INDEX result_stamp ON results(stamp);
                    PRAGMA application_id={ApplicationId}; PRAGMA user_version=2;
                    """;
                command.ExecuteNonQuery();
            }
            else if (application != ApplicationId || version is not (1 or 2)) throw new NotSupportedException("运行库标识或版本不受支持，未写入。");
            else if (version == 1)
            {
                // 新字段影响执行身份，旧程序不能忽略后继续发送；版本升级在一致性备份成功后进行。
                command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery();
            }
            transaction.Commit(); ProjectStore.Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;"); return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private void BackupLegacy(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt32(command.ExecuteScalar()) != ApplicationId) return;
        command.CommandText = "PRAGMA user_version"; if (Convert.ToInt32(command.ExecuteScalar()) != 1) return;
        // SQLite Backup API 包含已提交的 WAL 内容。备份失败直接阻止升级，不用文件复制猜测数据库状态。
        var directory = Path.Combine(paths.Root, "Backups"); Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(directory, "reference-runs-schema1-" + Guid.NewGuid().ToString("N") + ".db");
        using var backup = ProjectStore.Connect(backupPath, SqliteOpenMode.ReadWriteCreate);
        connection.BackupDatabase(backup);
    }

    public void Create(AnalysisRun run)
    {
        run.Validate();
        if (run.Version != 1 || run.State != AnalysisRunState.Queued || run.Nodes.Any(n => n.State != AnalysisNodeState.Pending)) throw new InvalidDataException("只能新建未运行的分析。");
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO runs VALUES($id,$book,1,$snapshot)";
        command.Parameters.AddWithValue("$id", run.Id.ToString()); command.Parameters.AddWithValue("$book", run.BookId.ToString());
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(run)); command.ExecuteNonQuery();
    }

    public AnalysisRun Read(Guid runId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot,version FROM runs WHERE id=$id"; command.Parameters.AddWithValue("$id", runId.ToString());
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new FileNotFoundException("分析运行不存在。");
        var run = Parse(reader.GetString(0));
        if (run.Id != runId || run.Version != reader.GetInt64(1)) throw new InvalidDataException("运行身份或版本损坏。");
        return run;
    }

    public IReadOnlyList<AnalysisRun> List(Guid bookId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM runs WHERE book=$book ORDER BY rowid DESC LIMIT 100"; command.Parameters.AddWithValue("$book", bookId.ToString());
        using var reader = command.ExecuteReader(); var results = new List<AnalysisRun>();
        while (reader.Read()) results.Add(Parse(reader.GetString(0))); return results;
    }

    public void Save(AnalysisRun run, long expectedVersion, AnalysisNodeResult? result = null)
    {
        run.Validate(); if (run.Version != expectedVersion + 1) throw new InvalidDataException("检查点版本必须递增一次。");
        if (result is not null)
        {
            Check(result);
            if (!run.Nodes.Any(n => n.Key == result.Key && n.InputStamp == result.InputStamp && n.State == AnalysisNodeState.Completed))
                throw new InvalidDataException("结果与已完成节点不一致。");
        }
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT snapshot FROM runs WHERE id=$id"; command.Parameters.AddWithValue("$id", run.Id.ToString());
        var previous = Parse(command.ExecuteScalar() as string ?? throw new FileNotFoundException("分析运行不存在。"));
        if (previous.SourceHash != run.SourceHash || previous.Connection != run.Connection || previous.StageSettings != run.StageSettings || previous.ReportReserve != run.ReportReserve || !previous.Chunks.SequenceEqual(run.Chunks) || previous.Budget.Id != run.Budget.Id ||
            previous.Target != run.Target || run.Budget.MaximumRequests < previous.Budget.MaximumRequests || run.Budget.MaximumTokens < previous.Budget.MaximumTokens || previous.Nodes.Length > run.Nodes.Length)
            throw new InvalidDataException("不能改写运行的来源、冻结配置、节点计划或降低预算。");
        if (run.Nodes.Length > previous.Nodes.Length && (previous.Nodes.Any(n => n.State != AnalysisNodeState.Completed) || run.Nodes.Skip(previous.Nodes.Length).Any(n => n.State != AnalysisNodeState.Pending)))
            throw new InvalidDataException("只能在完整阶段边界追加尚未执行的依赖节点。");
        for (var i = 0; i < previous.Nodes.Length; i++)
        {
            var old = previous.Nodes[i]; var next = run.Nodes[i];
            if (old.Key != next.Key || old.Kind != next.Kind || old.ChunkId != next.ChunkId || old.ExtractionPromptVersion != next.ExtractionPromptVersion ||
                old.Dimension != next.Dimension || old.Layer != next.Layer || !old.Selection.SequenceEqual(next.Selection) || !old.Dependencies.SequenceEqual(next.Dependencies) ||
                old.ExecutionConnection != next.ExecutionConnection || old.ExecutionPreset != next.ExecutionPreset ||
                next.FormatRetries < old.FormatRetries || next.FormatRetries > old.FormatRetries && (next.State != AnalysisNodeState.Pending || old.OperationId == next.OperationId) ||
                old.ReviewGuidance != next.ReviewGuidance && (old.State == AnalysisNodeState.Completed || next.State != AnalysisNodeState.Pending || old.OperationId == next.OperationId) ||
                old.State == AnalysisNodeState.Completed && CanonicalJson.Hash(old) != CanonicalJson.Hash(next) ||
                old.State != AnalysisNodeState.Completed && next.State == AnalysisNodeState.Completed && result?.Key != next.Key)
                throw new InvalidDataException("已完成结果不能覆盖；新完成状态必须和对应结果一起提交。");
        }
        command.Parameters.Clear();
        // SQL 条件同时守住身份和乐观版本，陈旧窗口不得把另一个运行的来源或预算覆盖进来。
        command.CommandText = "UPDATE runs SET snapshot=$snapshot,version=$next WHERE id=$id AND book=$book AND version=$previous AND json_extract(snapshot,'$.SourceId')=$source AND json_extract(snapshot,'$.PipelineVersion')=$pipeline";
        command.Parameters.AddWithValue("$id", run.Id.ToString()); command.Parameters.AddWithValue("$book", run.BookId.ToString()); command.Parameters.AddWithValue("$source", run.SourceId.ToString());
        command.Parameters.AddWithValue("$pipeline", run.PipelineVersion); command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(run));
        command.Parameters.AddWithValue("$next", run.Version); command.Parameters.AddWithValue("$previous", expectedVersion);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("运行检查点已改变，请重新读取。");
        if (result is not null)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO results VALUES($run,$key,$stamp,$json,$hash)";
            command.Parameters.AddWithValue("$run", run.Id.ToString()); command.Parameters.AddWithValue("$key", result.Key);
            command.Parameters.AddWithValue("$stamp", result.InputStamp); command.Parameters.AddWithValue("$json", result.Json); command.Parameters.AddWithValue("$hash", result.Hash); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public AnalysisNodeResult? ReadResult(Guid runId, string key) => QueryResult("run=$run AND key=$key", ("$run", runId.ToString()), ("$key", key));
    public AnalysisNodeResult? FindCached(string inputStamp) => QueryResult("stamp=$stamp", ("$stamp", inputStamp));
    private AnalysisNodeResult? QueryResult(string predicate, params (string Name, string Value)[] parameters)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT key,stamp,json,hash FROM results WHERE {predicate} ORDER BY rowid DESC LIMIT 1";
        foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Name, pair.Value);
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var result = new AnalysisNodeResult(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)); Check(result); return result;
    }
    private static void Check(AnalysisNodeResult result)
    {
        if (result.Json is null || result.Json.Length > 1000000 || result.InputStamp?.Length != 64 || AnalysisLimits.HashText(result.Json) != result.Hash)
            throw new InvalidDataException("节点结果内容或指纹损坏。");
    }
    private static AnalysisRun Parse(string json)
    {
        var run = JsonSerializer.Deserialize<AnalysisRun>(json) ?? throw new InvalidDataException("运行检查点为空。"); run.Validate(); return run;
    }
}
