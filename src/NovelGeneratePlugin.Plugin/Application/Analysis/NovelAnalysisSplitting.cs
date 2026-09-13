using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed partial class NovelAnalysisRunService
{
    /// <summary>
    /// 局部拆分只发生在提取阶段。输入预检不产生费用；输出截断只有已知用量才自动处理。
    /// 父节点历史、两个子节点和新切分在一个运行事务中保存，重启后可继续同一队列。
    /// </summary>
    private AnalysisRun? TrySplitNode(AnalysisRun run, ReferenceImport input, int index, ModelDiagnosticCode reason, CancellationToken ct, IProgress<AnalysisRun>? progress)
    {
        var parent = run.Nodes[index]; var options = run.Capacity ?? new();
        if (ct.IsCancellationRequested || !run.AllowAdaptiveSplit || parent.Kind != AnalysisNodeKind.Extraction ||
            run.Nodes.Any(n => n.Kind != AnalysisNodeKind.Extraction)) return null;
        var chunk = run.Chunks.Single(c => c.Id == parent.ChunkId);
        var depth = run.Splits.FirstOrDefault(s => s.Children.Any(c => c.Id == chunk.Id))?.Depth + 1 ?? 1;
        if (depth > options.MaximumSplitDepth || chunk.Body.Length < options.MinimumBodyCharacters * 2) return null;
        var entries = requests.List(run.Budget.Id); var entry = entries.SingleOrDefault(e => e.Id == parent.OperationId);
        if (reason == ModelDiagnosticCode.OutputLimit && entry is not { State: RequestState.Truncated, Usage.InputTokens: not null, Usage.OutputTokens: not null } ||
            reason == ModelDiagnosticCode.InputLimit && entry is not null ||
            entries.Any(e => e.Id != entry?.Id && !e.RetryAcknowledged && (e.State != RequestState.Completed || e.Usage.InputTokens is null || e.Usage.OutputTokens is null))) return null;
        ReferenceImport divided;
        try { divided = NovelTextPartitioner.Split(input, chunk.Id, options.MinimumBodyCharacters); }
        catch (InvalidDataException) { return null; }
        var children = divided.Chunks.Where(c => !run.Chunks.Any(old => old.Id == c.Id)).ToArray();
        var nodes = children.Select(c => parent with
        {
            Key = "chunk-" + c.Id.ToString("N"),
            ChunkId = c.Id,
            OperationId = Guid.NewGuid(),
            InputStamp = "",
            State = AnalysisNodeState.Pending,
            FormatRetries = 0
        }).ToArray();
        var next = run with
        {
            Chunks = divided.Chunks,
            Nodes = run.Nodes.RemoveAt(index).InsertRange(index, nodes),
            Splits = run.Splits.Add(new(parent, chunk, [.. children], depth, reason)),
            Version = run.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = "已将问题单元拆为两个子单元；已完成成果、父节点候选及费用保留。"
        };
        next.Validate(); AnalysisSplitRules.ValidateTransition(run, next);
        var remaining = next.Nodes.Count(n => n.State != AnalysisNodeState.Completed);
        if (next.Nodes.Length > 1000 || entries.Count + remaining + run.ReportReserve.Requests > run.Budget.MaximumRequests) return null;
        var empty = new Dictionary<string, AnalysisNodeResult>();
        var required = nodes.Sum(n => ModelRequestService.EstimateReservation(preparer.Prepare(next, divided, n, empty).Request));
        if (entries.Sum(e => e.ChargedTokens) + required + run.ReportReserve.Tokens > run.Budget.MaximumTokens) return null;
        // 此时除了当前已知截断，不存在其他未确认请求；不会顺带确认未知费用。
        if (entry is not null) requests.ReviseBudget(run.Budget, true);
        runs.Save(next, run.Version); Notify(progress, next); return next;
    }
}
