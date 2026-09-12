using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Models;
namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>预算预留与检查放在同一个 SQLite 写事务内，多个窗口/进程不能同时越过剩余预算。</summary>
public sealed class ModelRequestStore(WorkspacePaths paths) : IModelRequestStore
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root);
        var connection = ProjectStore.Connect(Path.Combine(paths.Root, "model-requests.db"), SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) is not (0 or 1)) throw new NotSupportedException("模型账本版本不受支持。");
            command.CommandText = "CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY,budget TEXT NOT NULL,snapshot TEXT NOT NULL); CREATE INDEX IF NOT EXISTS request_budget ON requests(budget); CREATE TABLE IF NOT EXISTS budgets(id TEXT PRIMARY KEY,requests INTEGER NOT NULL,tokens INTEGER NOT NULL); PRAGMA user_version=1;";
            command.ExecuteNonQuery(); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static List<ModelRequestEntry> Read(SqliteConnection connection, SqliteTransaction transaction, Guid budget)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT snapshot FROM requests WHERE budget=$budget"; command.Parameters.AddWithValue("$budget", budget.ToString());
        using var reader = command.ExecuteReader(); var entries = new List<ModelRequestEntry>();
        while (reader.Read()) entries.Add(JsonSerializer.Deserialize<ModelRequestEntry>(reader.GetString(0)) ?? throw new InvalidDataException("模型账本为空。"));
        return entries;
    }
    public IReadOnlyList<ModelRequestEntry> List(Guid budgetId)
    { using var connection = Open(); using var transaction = connection.BeginTransaction(); var entries = Read(connection, transaction, budgetId); transaction.Commit(); return entries; }
    public IReadOnlyList<ModelRequestEntry> Recent(Guid connectionId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM requests WHERE json_extract(snapshot,'$.ConnectionId')=$connection ORDER BY rowid DESC LIMIT 20";
        command.Parameters.AddWithValue("$connection", connectionId.ToString());
        using var reader = command.ExecuteReader(); var entries = new List<ModelRequestEntry>();
        while (reader.Read()) entries.Add(JsonSerializer.Deserialize<ModelRequestEntry>(reader.GetString(0)) ?? throw new InvalidDataException("模型账本为空。"));
        return entries;
    }
    public void Reserve(ModelRequestEntry entry, RequestBudget budget)
    {
        if (budget.Id == Guid.Empty || budget.MaximumRequests is < 1 or > 1000 || budget.MaximumTokens < 1 ||
            entry.BudgetId != budget.Id || entry.State != RequestState.Reserved || entry.ReservedTokens < 1) throw new InvalidDataException("请求预算无效。");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO budgets VALUES($id,$requests,$tokens)";
        command.Parameters.AddWithValue("$id", budget.Id.ToString()); command.Parameters.AddWithValue("$requests", budget.MaximumRequests); command.Parameters.AddWithValue("$tokens", budget.MaximumTokens); command.ExecuteNonQuery();
        command.CommandText = "SELECT requests,tokens FROM budgets WHERE id=$id";
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetInt32(0) != budget.MaximumRequests || reader.GetInt64(1) != budget.MaximumTokens) throw new InvalidOperationException("已建立的预算不能在请求中悄悄扩大。");
        var entries = Read(connection, transaction, budget.Id);
        if (entries.Any(e => e.State is RequestState.Reserved or RequestState.Running or RequestState.Uncertain or RequestState.Rejected or RequestState.Truncated))
            throw new InvalidOperationException("预算内有未完成、截断或失败请求，请先复核候选与用量后建立明确的新运行。");
        if (entries.Count >= budget.MaximumRequests || entries.Sum(e => e.ChargedTokens) + entry.ReservedTokens > budget.MaximumTokens)
            throw new InvalidOperationException("本次请求将超过运行预算，未发送。");
        command.Parameters.Clear(); command.CommandText = "INSERT INTO requests VALUES($id,$budget,$snapshot)";
        command.Parameters.AddWithValue("$id", entry.Id.ToString()); command.Parameters.AddWithValue("$budget", budget.Id.ToString()); command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(entry));
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public void Save(ModelRequestEntry entry)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE requests SET snapshot=$snapshot WHERE id=$id AND budget=$budget";
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(entry)); command.Parameters.AddWithValue("$id", entry.Id.ToString()); command.Parameters.AddWithValue("$budget", entry.BudgetId.ToString());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("模型请求尚未预留，不能写入结果。");
    }
}
