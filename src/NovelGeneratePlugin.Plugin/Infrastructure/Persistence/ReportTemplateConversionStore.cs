using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 一个转换是一份有界快照，同一事务核对旧版本并保存新状态。文件租约保护跨进程请求发送，
/// SQLite 的条件更新保护陈旧写入。独立恢复文件只用于补偿落盘失败，读取恢复不自动发起模型请求。
/// </summary>
public sealed class ReportTemplateConversionStore(WorkspacePaths paths) : IReportTemplateConversionStore
{
    private const int ApplicationId = 0x4E54434E;
    public string DatabasePath => Path.Combine(paths.Root, "report-template-conversions.db");
    public IDisposable Acquire(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("转换身份不能为空。", nameof(id));
        Directory.CreateDirectory(paths.Root);
        return new FileStream(Path.Combine(paths.Root, "template-conversion-" + id.ToString("N") + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(paths.Root);
        var connection = ProjectStore.Connect(DatabasePath, SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "PRAGMA application_id"; var application = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(command.ExecuteScalar());
            if (application == 0 && version == 0)
            {
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("转换库位置已被其他数据库占用。");
                command.CommandText = $"""
                    CREATE TABLE conversions(id TEXT PRIMARY KEY,run TEXT NOT NULL,revision INTEGER NOT NULL,snapshot TEXT NOT NULL,hash TEXT NOT NULL);
                    CREATE INDEX conversions_run ON conversions(run);
                    PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
            }
            else if (application != ApplicationId || version != 1) throw new NotSupportedException("转换库标识或版本不受支持，未写入。");
            transaction.Commit(); ProjectStore.Execute(connection, "PRAGMA synchronous=FULL;"); return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public void Create(ReportTemplateConversion conversion)
    {
        conversion.Validate();
        if (conversion.Revision != 1 || conversion.State != TemplateConversionState.Queued || conversion.Candidate is not null ||
            conversion.Steps.Any(s => s.State != TemplateConversionStepState.Pending || s.InputStamp != "" || s.Corrections != 0))
            throw new InvalidDataException("只能创建尚未执行的转换任务。");
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversions VALUES($id,$run,$revision,$snapshot,$hash)";
        Parameters(command, conversion); command.ExecuteNonQuery();
    }
    public ReportTemplateConversion Read(Guid id)
    { using var connection = Open(); return Read(connection, id); }
    private static ReportTemplateConversion Read(SqliteConnection connection, Guid id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,run,revision,snapshot,hash FROM conversions WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Parse(reader) : throw new FileNotFoundException("报告转模板任务不存在。");
    }
    private static ReportTemplateConversion Parse(SqliteDataReader reader)
    {
        var json = reader.GetString(3);
        if (json.Length > 8000000 || AnalysisLimits.HashText(json) != reader.GetString(4)) throw new InvalidDataException("转换快照指纹损坏。");
        var saved = JsonSerializer.Deserialize<ReportTemplateConversion>(json) ?? throw new InvalidDataException("转换快照为空。"); saved.Validate();
        if (saved.Id.ToString() != reader.GetString(0) || saved.Source.RunId.ToString() != reader.GetString(1) || saved.Revision != reader.GetInt64(2))
            throw new InvalidDataException("转换快照与索引身份不一致。");
        return saved;
    }
    public IReadOnlyList<ReportTemplateConversion> List(Guid runId, int offset = 0, int limit = 20)
    {
        if (runId == Guid.Empty || offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,run,revision,snapshot,hash FROM conversions WHERE run=$run ORDER BY rowid DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$run", runId.ToString()); command.Parameters.AddWithValue("$limit", limit); command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader(); var result = new List<ReportTemplateConversion>();
        while (reader.Read()) result.Add(Parse(reader)); return result;
    }
    public void Save(ReportTemplateConversion conversion, long expectedRevision)
    {
        conversion.Validate();
        if (conversion.Revision != expectedRevision + 1) throw new InvalidDataException("转换检查点版本必须递增一次。");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var previous = Read(connection, conversion.Id, transaction);
        ValidateTransition(previous, conversion);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE conversions SET snapshot=$snapshot,hash=$hash,revision=$revision WHERE id=$id AND run=$run AND revision=$expected";
        Parameters(command, conversion); command.Parameters.AddWithValue("$expected", expectedRevision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("转换检查点已变化，请重新读取。");
        transaction.Commit();
    }
    private static object Identity(ReportTemplateConversion value) => new
    {
        value.Id,
        value.TemplateId,
        value.Source,
        value.Name,
        value.Dimensions,
        value.Connection,
        value.Preset,
        value.ContextTokens,
        BudgetId = value.Budget.Id,
        value.ContractVersion,
        value.CreatedAt,
        Steps = value.Steps.Select(s => new { s.Key, s.Kind, s.Dimensions, s.SourceIds, s.Dependencies })
    };
    internal static void ValidateTransition(ReportTemplateConversion previous, ReportTemplateConversion next)
    {
        if (CanonicalJson.Hash(Identity(previous)) != CanonicalJson.Hash(Identity(next)) ||
            next.Budget.MaximumRequests < previous.Budget.MaximumRequests || next.Budget.MaximumTokens < previous.Budget.MaximumTokens ||
            next.UpdatedAt < previous.UpdatedAt || previous.State == TemplateConversionState.DraftSaved ||
            previous.Candidate is not null && CanonicalJson.Hash(previous.Candidate) != CanonicalJson.Hash(next.Candidate))
            throw new InvalidDataException("不能改写转换来源、固定计划、已保存候选或已交付任务。");
        for (var i = 0; i < previous.Steps.Length; i++)
        {
            var old = previous.Steps[i]; var current = next.Steps[i];
            if (old.State == TemplateConversionStepState.Completed && CanonicalJson.Hash(old) != CanonicalJson.Hash(current) ||
                current.Corrections < old.Corrections ||
                current.OperationId != old.OperationId && current.State != TemplateConversionStepState.Pending ||
                current.OperationId == old.OperationId && old.InputStamp != "" && old.InputStamp != current.InputStamp ||
                current.Corrections > old.Corrections && current.OperationId == old.OperationId ||
                current.State == TemplateConversionStepState.Completed && current.Dependencies.Any(key => next.Steps.Single(s => s.Key == key).State != TemplateConversionStepState.Completed))
                throw new InvalidDataException("不能覆盖成功步骤或修改在途请求身份。");
        }
    }
    private static void Parameters(SqliteCommand command, ReportTemplateConversion conversion)
    {
        var json = JsonSerializer.Serialize(conversion);
        if (json.Length > 8000000) throw new InvalidDataException("转换快照超过保存容量。");
        command.Parameters.AddWithValue("$id", conversion.Id.ToString()); command.Parameters.AddWithValue("$run", conversion.Source.RunId.ToString());
        command.Parameters.AddWithValue("$revision", conversion.Revision); command.Parameters.AddWithValue("$snapshot", json);
        command.Parameters.AddWithValue("$hash", AnalysisLimits.HashText(json));
    }
    private string RecoveryPath(Guid id) => Path.Combine(paths.RecoveryDirectory, "TemplateConversions", id.ToString("N") + ".json");
    public void WriteRecovery(ReportTemplateConversion conversion)
    {
        conversion.Validate(); var path = RecoveryPath(conversion.Id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(conversion);
        if (json.Length > 8000000) throw new InvalidDataException("转换恢复内容超过容量。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, conversion); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public ReportTemplateConversion? ReadRecovery(Guid id)
    {
        var path = RecoveryPath(id); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 32000000) throw new InvalidDataException("转换恢复文件超过容量。");
        using var stream = File.OpenRead(path);
        var value = JsonSerializer.Deserialize<ReportTemplateConversion>(stream) ?? throw new InvalidDataException("转换恢复文件为空。");
        value.Validate(); if (value.Id != id) throw new InvalidDataException("转换恢复文件身份不一致。"); return value;
    }
    public void DeleteRecovery(Guid id) { try { File.Delete(RecoveryPath(id)); } catch (DirectoryNotFoundException) { } }
}
