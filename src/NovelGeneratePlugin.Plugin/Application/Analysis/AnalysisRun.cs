using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public enum AnalysisRunState { Queued, Running, Paused, NeedsAttention, ExtractionCompleted, Completed, Cancelled, IntegrationCompleted }
public enum AnalysisTarget { Extraction, Integration, Report }
public enum AnalysisNodeKind { Extraction, Integration, Summary, Dimension, Synthesis }
public enum AnalysisNodeState { Pending, Running, Completed }

/// <summary>节点保存输入依赖与先于发送落盘的操作 ID。重试更换操作 ID，旧请求及其费用仍留在同一总账本。</summary>
public sealed record AnalysisNode(string Key, AnalysisNodeKind Kind, Guid? ChunkId, ImmutableArray<string> Dependencies,
    Guid OperationId, string InputStamp, AnalysisNodeState State)
{
    public string ExtractionPromptVersion { get; init; } = "v2";
    public AnalysisDimension? Dimension { get; init; }
    public ImmutableArray<int> Selection { get; init; } = [];
    public int Layer { get; init; }
    public string ReviewGuidance { get; init; } = "";
    public int FormatRetries { get; init; }
    public FrozenConnection? ExecutionConnection { get; init; }
    public ModelPreset? ExecutionPreset { get; init; }
}
public sealed record AnalysisStageReserve(int Requests, long Tokens);
public sealed record AnalysisNodeResult(string Key, string InputStamp, string Json, string Hash);

/// <summary>
/// 运行属于不可变参考来源，不属于正在创作的小说。结果分行保存，频繁写检查点不重新序列化整本原文或已完成正文。
/// PipelineVersion 冻结执行算法；升级后旧任务只读，新任务按各节点指纹复用仍有效的结果。
/// </summary>
public sealed record AnalysisRun(Guid Id, Guid BookId, Guid SourceId, string SourceHash, string PipelineVersion,
    FrozenConnection Connection, RequestBudget Budget, AnalysisStageReserve ReportReserve, ImmutableArray<AnalysisNode> Nodes,
    AnalysisRunState State, string Message, long Version, DateTimeOffset UpdatedAt)
{
    public ImmutableArray<AnalysisChunk> Chunks { get; init; } = [];
    public AnalysisTarget Target { get; init; }
    public bool AllowFormatRetry { get; init; }
    public AnalysisStageSettings? StageSettings { get; init; }
    public AnalysisCapacityOptions? Capacity { get; init; }
    public bool AllowAdaptiveSplit { get; init; }
    public ImmutableArray<AnalysisSplit> Splits { get; init; } = [];
    public void Validate()
    {
        Connection.Connection.Validate(); Connection.Preset.Validate();
        StageSettings?.Validate(Connection.Connection.Settings.Provider);
        Capacity?.Validate();
        if (Splits.IsDefault || Splits.Length > 999 || Splits.Any(s => s is null || s.Parent is null || s.Original is null || s.Children.IsDefault || s.Children.Length != 2 ||
            s.Depth is < 1 or > 8 || s.Reason is not (ModelDiagnosticCode.InputLimit or ModelDiagnosticCode.OutputLimit)))
            throw new InvalidDataException("拆分历史无效。");
        if (Id == Guid.Empty || BookId == Guid.Empty || SourceId == Guid.Empty || SourceHash?.Length != 64 || string.IsNullOrWhiteSpace(PipelineVersion) ||
            Connection.BookId != BookId || Connection.Task != ModelTask.Checking || Connection.Preset != Connection.Connection.Settings.Preset(Connection.Task) ||
            Budget.Id == Guid.Empty || Budget.MaximumRequests is < 1 or > 1000 || Budget.MaximumTokens < 1 || ReportReserve.Requests < 0 || ReportReserve.Tokens < 0 ||
            ReportReserve.Requests >= Budget.MaximumRequests || ReportReserve.Tokens >= Budget.MaximumTokens || Version < 1 ||
            !Enum.IsDefined(State) || !Enum.IsDefined(Target) || Message is null || Message.Length > 2000 || Nodes.IsDefaultOrEmpty || Nodes.Length > 1000 ||
            Nodes.Any(n => n is null) || Chunks.IsDefaultOrEmpty || Chunks.Any(c => c is null) || Chunks.Length != Nodes.Count(n => n.Kind == AnalysisNodeKind.Extraction) ||
            Nodes.Where(n => n.Kind == AnalysisNodeKind.Extraction).Select(n => n.ChunkId).Distinct().Count() != Chunks.Length || Chunks.Select(c => c.Id).Distinct().Count() != Chunks.Length)
            throw new InvalidDataException("分析运行身份、预算或状态无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var operations = new HashSet<Guid>();
        foreach (var node in Nodes)
        {
            if (node is null) throw new InvalidDataException("分析节点不能为空。");
            var frozen = node.ExecutionConnection ?? Connection;
            frozen.Connection.Validate(); frozen.Preset.Validate(); node.ExecutionPreset?.Validate();
            if (frozen.BookId != BookId || frozen.Task != ModelTask.Checking || frozen.Preset != frozen.Connection.Settings.Checking)
                throw new InvalidDataException("节点冻结身份或检查预设不一致。");
            if (node is null || string.IsNullOrWhiteSpace(node.Key) || node.Key.Length > 100 || !Enum.IsDefined(node.Kind) || !Enum.IsDefined(node.State) ||
                node.Dependencies.IsDefault || node.Dependencies.Any(key => !seen.Contains(key)) || !seen.Add(node.Key) ||
                node.OperationId == Guid.Empty || !operations.Add(node.OperationId) || node.InputStamp is null ||
                node.State != AnalysisNodeState.Pending && node.InputStamp.Length != 64 || node.Selection.IsDefault || node.Selection.Length > (node.Kind == AnalysisNodeKind.Summary ? 80 : 60) || node.Layer is < 0 or > 16 || node.ReviewGuidance is null || node.ReviewGuidance.Length > 2000 ||
                node.Selection.Any(id => id < 1) || node.Selection.Distinct().Count() != node.Selection.Length || node.Dimension is { } dimension && !Enum.IsDefined(dimension) ||
                node.Kind == AnalysisNodeKind.Integration && node.Selection.IsEmpty || node.Kind == AnalysisNodeKind.Dimension && node.Dimension is null ||
                node.FormatRetries is < 0 or > 1 ||
                node.Kind == AnalysisNodeKind.Extraction && (node.ChunkId is null || !Chunks.Any(c => c.Id == node.ChunkId) || node.ExtractionPromptVersion is not ("v2" or "v3" or "v4")))
                throw new InvalidDataException("分析节点身份、依赖顺序或输入指纹无效。");
        }
        if (State == AnalysisRunState.Completed && Nodes.Any(n => n.State != AnalysisNodeState.Completed) ||
            State == AnalysisRunState.ExtractionCompleted && Nodes.Any(n => n.Kind == AnalysisNodeKind.Extraction && n.State != AnalysisNodeState.Completed) ||
            State == AnalysisRunState.IntegrationCompleted && Nodes.Any(n => n.State != AnalysisNodeState.Completed))
            throw new InvalidDataException("未完成节点不能声明分析完成。");
    }
}

/// <summary>仅承载小说分析检查点。保存节点结果与运行状态必须处于同一本地事务；租约防止跨进程重复发送。</summary>
public interface IAnalysisRunStore
{
    IDisposable Acquire(Guid runId);
    void Create(AnalysisRun run);
    AnalysisRun Read(Guid runId);
    IReadOnlyList<AnalysisRun> List(Guid bookId);
    void Save(AnalysisRun run, long expectedVersion, AnalysisNodeResult? result = null);
    AnalysisNodeResult? ReadResult(Guid runId, string key);
    AnalysisNodeResult? FindCached(string inputStamp);
}

public sealed class AnalysisRunControl
{
    private int _pause;
    public bool PauseRequested => Volatile.Read(ref _pause) != 0;
    public void Pause() => Interlocked.Exchange(ref _pause, 1);
}
