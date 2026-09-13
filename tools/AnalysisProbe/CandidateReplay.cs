using System.Text.Json;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 对指定历史请求复现真实契约错误。调用参数绑定来源、运行、节点与账本操作 ID，
/// 原库仅作只读一致性备份；不读取凭据、不创建模型、不改写正式候选或复核费用。
/// </summary>
internal static class CandidateReplay
{
    private sealed record Input(string WorkspaceRoot, Guid RunId, string NodeKey, Guid OperationId);
    public static string Run(string inputFile, string output)
    {
        var options = JsonSerializer.Deserialize<Input>(File.ReadAllText(inputFile)) ?? throw new InvalidDataException("回放参数为空。");
        var paths = ResponseReplay.CopyWorkspace(options.WorkspaceRoot, output);
        var run = new AnalysisRunStore(paths).Read(options.RunId);
        var node = run.Nodes.Single(n => n.Key == options.NodeKey && n.Kind == AnalysisNodeKind.Extraction);
        var entry = new ModelRequestStore(paths).List(run.Budget.Id).Single(e => e.Id == options.OperationId && e.BookId == run.BookId);
        var source = new ReferenceSourceStore(paths).Read(run.BookId) with { Chunks = run.Chunks }; source.Validate();
        var chunk = run.Chunks.Single(c => c.Id == node.ChunkId);
        var passages = ChunkAnalysisContract.Passages(source.Source, chunk);
        var contract = new ChunkAnalysisContract(source.Source, chunk, entry.Id, node.InputStamp, passages,
            node.ExtractionPromptVersion is "v4" or "v5", node.ExtractionPromptVersion == "v5");
        ModelDiagnostic? diagnostic = null;
        try { ModelRequestService.ValidateJson(ModelRequestService.RepairJsonWrapper(entry.PartialText), contract); }
        catch (ModelRequestException error) { if (error.Diagnostic is null) throw; diagnostic = error.Diagnostic; }
        var json = JsonSerializer.Serialize(new
        {
            NetworkCalls = 0, SourceOpenedReadOnly = true, options.RunId, options.NodeKey, options.OperationId,
            entry.Usage, entry.ChargedTokens, entry.ResponseComplete, PassageCount = passages.Length,
            Diagnostic = diagnostic,
            Scope = "只验证指定节点范围中的候选结构及原文坐标；历史节点可能已换操作ID，未证明原请求输入指纹，也未采纳候选。"
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "candidate-result.json"), json); return json;
    }
}
