using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 对真实运行做离线回放：源库以只读方式打开，SQLite 一致性备份到调用方的新目录。
/// 不复制凭据，不创建模型客户端，不重试、不采纳、不修改用户运行；输出只有脱敏诊断和统计。
/// </summary>
internal static class ResponseReplay
{
    internal static WorkspacePaths CopyWorkspace(string sourceRoot, string output)
    {
        var copied = Path.Combine(output, "workspace"); Directory.CreateDirectory(copied);
        foreach (var name in new[] { "reference-analysis.db", "reference-runs.db", "model-requests.db" })
        {
            using var source = ProjectStore.Connect(Path.Combine(sourceRoot, name), SqliteOpenMode.ReadOnly);
            using var destination = ProjectStore.Connect(Path.Combine(copied, name), SqliteOpenMode.ReadWriteCreate);
            source.BackupDatabase(destination);
        }
        return new(copied);
    }
    public static string Run(string sourceRoot, string output)
    {
        var paths = CopyWorkspace(sourceRoot, output); var sources = new ReferenceSourceStore(paths); var runs = new AnalysisRunStore(paths);
        var ledger = new ModelRequestStore(paths); var preparer = new NovelAnalysisNodePreparer(); var outcomes = new List<object>();
        foreach (var book in sources.List())
        {
            foreach (var run in runs.List(book.Id).Take(1))
            {
                var input = sources.Read(book.Id) with { Chunks = run.Chunks }; input.Validate();
                var results = new Dictionary<string, AnalysisNodeResult>();
                foreach (var node in run.Nodes.Where(n => n.State == AnalysisNodeState.Completed))
                {
                    var prepared = preparer.Prepare(run, input, node, results);
                    var result = runs.ReadResult(run.Id, node.Key) ?? throw new InvalidDataException("已完成节点缺少结果。");
                    if (prepared.InputStamp != result.InputStamp || result.InputStamp != node.InputStamp || AnalysisLimits.HashText(result.Json) != result.Hash)
                        throw new InvalidDataException("旧节点输入或结果哈希不一致。");
                    ModelRequestService.ValidateJson(result.Json, prepared.Request.Contract); results.Add(node.Key, result);
                }
                var chunk = input.Chunks[0];
                var schema = new ChunkAnalysisContract(input.Source, chunk, Guid.NewGuid(), "", ChunkAnalysisContract.Passages(input.Source, chunk)).JsonSchema;
                var entries = ledger.List(run.Budget.Id); var candidates = new List<object>();
                foreach (var entry in entries.Where(e => e.State != RequestState.Completed))
                {
                    string shape;
                    try { ModelRequestService.ValidateJson(ModelRequestService.RepairJsonWrapper(entry.PartialText), new Shape(schema)); shape = "结构字段通过（未证明原文引用或流完整）"; }
                    catch (ModelRequestException error) { shape = error.Diagnostic?.Code.ToString() ?? error.Failure.ToString(); }
                    candidates.Add(new { entry.Id, entry.State, entry.Usage, OutputCharacters = entry.PartialText.Length, Shape = shape });
                }
                outcomes.Add(new { run.Id, SourceCharacters = input.Source.Text.Length, ReplayedCompletedNodes = results.Count,
                    Requests = entries.Count, ConservativeTokens = entries.Sum(e => e.ChargedTokens), Candidates = candidates });
            }
        }
        var json = JsonSerializer.Serialize(new { NetworkCalls = 0, SourceOpenedReadOnly = true, Runs = outcomes }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "replay-result.json"), json); return json;
    }
    private sealed class Shape(string schema) : IModelOutputContract
    {
        public string JsonSchema => schema;
        public void Validate(JsonElement value)
        {
            _ = JsonSerializer.Deserialize<ChunkOutput>(value.GetRawText(), new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
                Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
            }) ?? throw new InvalidDataException("提取结构为空。");
        }
    }
}
