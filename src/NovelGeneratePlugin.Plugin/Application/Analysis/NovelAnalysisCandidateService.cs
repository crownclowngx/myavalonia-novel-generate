using NovelGeneratePlugin.Application.Models;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record AnalysisCandidateReview(string Node, string Failure, string Validation, ModelRequestEntry Entry);

/// <summary>只读复核使用与运行相同的请求构造器与契约；展示具体失败原因不修改账本，也不隐式允许付费重试。</summary>
public sealed class NovelAnalysisCandidateService(IReferenceSourceStore sources, IAnalysisRunStore runs, ModelRequestService requests, IAnalysisNodePreparer preparer)
{
    public AnalysisCandidateReview Read(Guid runId)
    {
        var run = runs.Read(runId); var node = run.Nodes.FirstOrDefault(n => n.State != AnalysisNodeState.Completed)
            ?? throw new InvalidOperationException("所有节点已完成，没有待复核候选。");
        var entry = requests.List(run.Budget.Id).SingleOrDefault(e => e.Id == node.OperationId)
            ?? throw new InvalidOperationException("该节点尚未发出请求，没有模型候选。");
        var validation = "";
        try
        {
            var source = sources.Read(run.BookId) with { Chunks = run.Chunks };
            var results = run.Nodes.Where(n => n.State == AnalysisNodeState.Completed).ToDictionary(n => n.Key,
                n => runs.ReadResult(run.Id, n.Key) ?? throw new InvalidDataException("前置节点结果缺失。"));
            var prepared = preparer.Prepare(run, source, node, results);
            if (prepared.InputStamp != node.InputStamp) throw new InvalidDataException("当前输入指纹已经变化，需要新修订运行。");
            ModelRequestService.ValidateJson(ModelRequestService.RepairJsonWrapper(entry.PartialText), prepared.Request.Contract);
            validation = "当前本地结构校验通过；仍须核对请求是否完整及语义是否正确，不能据此认定质量通过。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { validation = error.Message; }
        return new(node.Key, entry.Diagnostic?.Message ?? entry.Failure ?? entry.State.ToString(), validation, entry);
    }
}
