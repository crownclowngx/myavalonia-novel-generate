using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record PreparedAnalysisNode(TextModelRequest Request, string InputStamp);

/// <summary>模型任务只构造有界请求；调度服务统一拥有付费、重放、缓存与提交，后续专题不得绕过此边界自行发送。</summary>
public interface IAnalysisNodePreparer
{
    PreparedAnalysisNode Prepare(AnalysisRun run, ReferenceImport input, AnalysisNode node, IReadOnlyDictionary<string, AnalysisNodeResult> dependencies);
}

public sealed class NovelAnalysisNodePreparer : IAnalysisNodePreparer
{
    public PreparedAnalysisNode Prepare(AnalysisRun run, ReferenceImport input, AnalysisNode node, IReadOnlyDictionary<string, AnalysisNodeResult> dependencies)
    {
        // 完成节点可以来自旧修订。保留其原执行身份，不能用新参数重写缓存来源。
        var effectivePreset = node.ExecutionPreset;
        run = run with { Connection = node.ExecutionConnection ?? run.Connection };
        PreparedAnalysisNode prepared;
        if (node.Kind == AnalysisNodeKind.Integration) prepared = NovelIntegrationRequests.Prepare(run, input, node, dependencies);
        else if (node.Kind is AnalysisNodeKind.Summary or AnalysisNodeKind.Dimension or AnalysisNodeKind.Synthesis) prepared = NovelReportRequests.Prepare(run, input, node, dependencies);
        else
        {
            if (node.Kind != AnalysisNodeKind.Extraction) throw new NotSupportedException("此版本尚未实现该分析阶段。");
            var chunk = input.Chunks.Single(c => c.Id == node.ChunkId);
            var extracted = NovelChunkAnalysisService.Prepare(input, chunk, run.Connection, node.OperationId, node.ExtractionPromptVersion);
            prepared = new(extracted.Request, extracted.InputStamp);
        }
        if (effectivePreset is not null)
            prepared = new(prepared.Request with { ExecutionPreset = effectivePreset }, CanonicalJson.Hash(new { prepared.InputStamp, ExecutionPreset = effectivePreset }));
        if (node.ReviewGuidance.Length == 0) return prepared;
        return new(prepared.Request with { SystemPrompt = prepared.Request.SystemPrompt + "\n本次复核后的补充约束：" + node.ReviewGuidance },
            CanonicalJson.Hash(new { prepared.InputStamp, node.ReviewGuidance }));
    }
}

/// <summary>
/// 全文调度仅负责节点顺序和恢复，不承担提取提示或数据库 SQL。每个运行一个系统租约、一个总预算。
/// 先存操作 ID，再发送；请求账本完成而节点未提交时，只在本地重放同一响应，不再调用远端。
/// </summary>
public sealed class NovelAnalysisRunService(IReferenceSourceStore sources, IAnalysisRunStore runs, ConnectionService connections,
    ModelRequestService requests, IAnalysisNodePreparer preparer)
{
    public const string PipelineVersion = "novel-analysis-g32-v1";
    public async Task<AnalysisRun> CreateAsync(Guid bookId, ConnectionBinding binding, int maximumRequests, long maximumTokens,
        AnalysisStageReserve reserve, CancellationToken ct, PartitionOptions? partition = null, Guid? previousRunId = null, AnalysisTarget target = AnalysisTarget.Extraction,
        AnalysisStageSettings? stageSettings = null)
    {
        var previous = previousRunId is Guid previousId ? runs.Read(previousId) : null;
        using var revisionLease = previous is null ? null : runs.Acquire(previous.Budget.Id);
        if (previous is not null)
        {
            previous = runs.Read(previous.Id);
            if (previous.BookId != bookId || maximumRequests < previous.Budget.MaximumRequests || maximumTokens < previous.Budget.MaximumTokens)
                throw new InvalidOperationException("修订必须属于同一来源并保留原预算与累计费用。");
        }
        var input = await Task.Run(() => sources.Read(bookId), ct).ConfigureAwait(false);
        if (previous is not null) input = input with { Chunks = previous.Chunks };
        if (partition is not null) input = NovelTextPartitioner.Rechunk(input, partition, ct);
        var frozen = await connections.FreezeAsync(binding, bookId, ModelTask.Checking).ConfigureAwait(false);
        stageSettings?.Validate(frozen.Connection.Settings.Provider);
        if (input.Chunks.Length + reserve.Requests > maximumRequests || maximumRequests > 1000)
            throw new InvalidOperationException("全文单元与后续阶段所需请求超过总额；请调整切分或额度，上限为 1000，尚未发送请求。");
        var id = Guid.NewGuid();
        var nodes = input.Chunks.Select(c => new AnalysisNode("chunk-" + c.Id.ToString("N"), AnalysisNodeKind.Extraction, c.Id, [], Guid.NewGuid(), "", AnalysisNodeState.Pending)
        {
            // 修订可以只加强未完成单元的提示，已完成且范围不变的单元仍引用原提示版本，避免重新付费。
            ExtractionPromptVersion = previous?.Nodes.FirstOrDefault(n => n.ChunkId == c.Id && n.State == AnalysisNodeState.Completed)?.ExtractionPromptVersion ?? NovelChunkAnalysisService.CurrentPromptVersion,
            ReviewGuidance = previous?.Nodes.FirstOrDefault(n => n.ChunkId == c.Id && n.State == AnalysisNodeState.Completed)?.ReviewGuidance ?? "",
            ExecutionConnection = previous?.Nodes.FirstOrDefault(n => n.ChunkId == c.Id && n.State == AnalysisNodeState.Completed) is { } completed ? completed.ExecutionConnection ?? previous.Connection : frozen,
            ExecutionPreset = previous?.Nodes.FirstOrDefault(n => n.ChunkId == c.Id && n.State == AnalysisNodeState.Completed) is { } saved ? saved.ExecutionPreset : stageSettings?.Extraction
        }).ToImmutableArray();
        // 同源同冻结配置且全部提取已完成时，修订的下层输入确定不变，可继承已有整合计划与逐节点复核说明。
        // 来源切分或模型改变则重新规划；每个继承节点仍要按当前依赖指纹校验缓存，不能仅凭旧状态跳过。
        if (target != AnalysisTarget.Extraction && previous is not null && previous.PipelineVersion == PipelineVersion &&
            previous.Chunks.SequenceEqual(input.Chunks) && previous.Nodes.Where(n => n.Kind == AnalysisNodeKind.Extraction).All(n => n.State == AnalysisNodeState.Completed))
            nodes = nodes.AddRange(previous.Nodes.Where(n => n.Kind == AnalysisNodeKind.Integration || target == AnalysisTarget.Report && n.Kind != AnalysisNodeKind.Extraction)
                .Select(n => n with
                {
                    OperationId = Guid.NewGuid(),
                    State = AnalysisNodeState.Pending,
                    InputStamp = "",
                    ExecutionConnection = n.State == AnalysisNodeState.Completed ? n.ExecutionConnection ?? previous.Connection : frozen,
                    ExecutionPreset = n.State == AnalysisNodeState.Completed ? n.ExecutionPreset : stageSettings?.For(n.Kind)
                }));
        var run = new AnalysisRun(id, bookId, input.Source.Id, input.Source.TextHash, PipelineVersion, frozen, new(previous?.Budget.Id ?? id, maximumRequests, maximumTokens), reserve,
            nodes, AnalysisRunState.Queued, "已建立全文提取队列。", 1, DateTimeOffset.UtcNow)
        { Chunks = input.Chunks, Target = target, AllowFormatRetry = true, StageSettings = stageSettings };
        run.Validate();
        // 防止首个请求就侵占后续阶段额度。总 token 不做虚假的精确承诺，每次请求仍按实际账本重新核对。
        var first = preparer.Prepare(run, input, nodes[0], new Dictionary<string, AnalysisNodeResult>());
        if (ModelRequestService.EstimateReservation(first.Request) + reserve.Tokens > maximumTokens)
            throw new InvalidOperationException("预算不足以发送首个单元并保留后续报告额度。");
        ct.ThrowIfCancellationRequested();
        if (previous is not null) requests.ReviseBudget(run.Budget, false);
        await Task.Run(() => runs.Create(run), ct).ConfigureAwait(false);
        if (previous is not null && previous.State is not (AnalysisRunState.Completed or AnalysisRunState.ExtractionCompleted or AnalysisRunState.IntegrationCompleted))
            SaveState(previous, AnalysisRunState.Cancelled, "已建立修订运行；旧结果和累计费用保留。", null);
        return run;
    }

    public AnalysisRun Read(Guid runId) => runs.Read(runId);
    public IReadOnlyList<AnalysisRun> List(Guid bookId) => runs.List(bookId);
    public IReadOnlyList<ModelRequestEntry> Usage(Guid runId) => requests.List(runs.Read(runId).Budget.Id);

    public NovelIntegrationSnapshot ReadIntegrated(Guid runId)
    {
        var run = runs.Read(runId); var input = sources.Read(run.BookId) with { Chunks = run.Chunks }; input.Validate();
        if (run.Target == AnalysisTarget.Extraction || run.Nodes.Any(n => n.State != AnalysisNodeState.Completed)) throw new InvalidOperationException("全书整合尚未完成。");
        var results = run.Nodes.ToDictionary(n => n.Key, n => runs.ReadResult(run.Id, n.Key) ?? throw new InvalidDataException("已完成节点缺少结果。"));
        var index = NovelAnalysisIndex.Build(run, input, results); return NovelIntegrationSnapshot.Build(run, index, results);
    }

    public Task<AnalysisRun> ExecuteAsync(Guid runId, AnalysisRunControl control, IProgress<AnalysisRun>? progress, CancellationToken ct) =>
        Task.Run(() => ExecuteCoreAsync(runId, control, progress, ct), CancellationToken.None);

    private async Task<AnalysisRun> ExecuteCoreAsync(Guid runId, AnalysisRunControl control, IProgress<AnalysisRun>? progress, CancellationToken ct)
    {
        var run = runs.Read(runId); using var lease = runs.Acquire(run.Budget.Id); run = runs.Read(runId);
        if (run.State is AnalysisRunState.Cancelled or AnalysisRunState.Completed or AnalysisRunState.ExtractionCompleted or AnalysisRunState.IntegrationCompleted) return run;
        try
        {
            if (run.PipelineVersion != PipelineVersion) throw new InvalidOperationException("分析算法版本已变化，请新建运行；旧结果保留供阅读和有效缓存复用。");
            var input = sources.Read(run.BookId) with { Chunks = run.Chunks }; input.Validate();
            if (input.Source.Id != run.SourceId || input.Source.TextHash != run.SourceHash) throw new InvalidDataException("来源版本与运行不一致。");
            var completed = new Dictionary<string, AnalysisNodeResult>(StringComparer.Ordinal);
            while (true)
            {
                for (var index = 0; index < run.Nodes.Length; index++)
                {
                    var node = run.Nodes[index];
                    if (node.Dependencies.Any(key => !completed.ContainsKey(key))) throw new InvalidDataException("前置分析节点尚未有效完成。");
                    var prepared = preparer.Prepare(run, input, node, completed);
                    if (node.State != AnalysisNodeState.Pending && node.InputStamp != prepared.InputStamp) throw new InvalidDataException("节点输入依赖已失效，请建立新版本运行。");
                    if (node.State == AnalysisNodeState.Completed)
                    {
                        var stored = runs.ReadResult(run.Id, node.Key) ?? throw new InvalidDataException("已完成节点的结果缺失。");
                        Validate(stored, prepared); completed[node.Key] = stored; continue;
                    }
                    if (ct.IsCancellationRequested) return SaveState(run, AnalysisRunState.Cancelled, "分析已取消；已完成成果保留，未完成请求需复核。", progress);
                    if (control.PauseRequested) return SaveState(run, AnalysisRunState.Paused, "已在节点边界暂停。", progress);

                    var entry = requests.List(run.Budget.Id).SingleOrDefault(e => e.Id == node.OperationId);
                    string? json = null;
                    if (entry is not null)
                    {
                        // 缺失用量不妨碍恢复真实完整响应，但后续新请求仍由总账本阻止，不能把未知费用当成零。
                        if (entry.State != RequestState.Completed)
                        {
                            var corrected = TryScheduleCorrection(run, index, prepared, ct, progress);
                            if (corrected is not null) { run = corrected; index--; continue; }
                            return SaveState(run, AnalysisRunState.NeedsAttention, entry.Diagnostic?.Message ??
                                "此节点存在未完成、截断或失败请求。请复核候选和费用后明确重试，未自动重发。", progress);
                        }
                        if (entry.BookId != run.BookId || entry.ConnectionId != prepared.Request.Configuration.Connection.Id || entry.ConnectionVersion != prepared.Request.Configuration.Connection.Version || entry.Model != prepared.Request.EffectivePreset.Model)
                            throw new InvalidDataException("请求账本与分析节点不一致。");
                        json = ModelRequestService.RepairJsonWrapper(entry.PartialText);
                    }
                    else
                    {
                        var cached = runs.FindCached(prepared.InputStamp);
                        if (cached is not null) { Validate(cached, prepared); json = cached.Json; }
                    }
                    if (json is null)
                    {
                        ct.ThrowIfCancellationRequested(); await connections.ValidateCurrentAsync(prepared.Request.Configuration).ConfigureAwait(false);
                        CheckStageBudget(run, node.Kind, prepared.Request);
                        node = node with { State = AnalysisNodeState.Running, InputStamp = prepared.InputStamp };
                        run = SaveNode(run, index, node, null, progress);
                        TextModelResponse response;
                        try { response = await requests.GenerateAsync(prepared.Request, run.Budget, null, ct).ConfigureAwait(false); }
                        catch (ModelRequestException)
                        {
                            var corrected = TryScheduleCorrection(run, index, prepared, ct, progress);
                            if (corrected is null) throw;
                            run = corrected; index--; continue;
                        }
                        if (response.Completion != ModelCompletion.Complete) return SaveState(run, AnalysisRunState.NeedsAttention, "节点输出截断；候选和费用已保留，请复核切分与模型输出额度。", progress);
                        json = response.Text;
                    }
                    ModelRequestService.ValidateJson(json, prepared.Request.Contract);
                    var result = new AnalysisNodeResult(node.Key, prepared.InputStamp, json, AnalysisLimits.HashText(json));
                    node = node with { State = AnalysisNodeState.Completed, InputStamp = prepared.InputStamp };
                    // 即使用户恰在响应后取消，也先完成不可分割的本地提交，再在下个边界停下。
                    run = SaveNode(run, index, node, result, progress); completed.Add(node.Key, result);
                }
                if (run.Target == AnalysisTarget.Extraction) break;
                var fullIndex = NovelAnalysisIndex.Build(run, input, completed);
                var plan = run.Nodes.Any(n => n.Kind == AnalysisNodeKind.Integration) ? ImmutableArray<AnalysisNode>.Empty : NovelIntegrationPlanner.Build(run, fullIndex);
                if (plan.IsEmpty && run.Target == AnalysisTarget.Report)
                    plan = NovelReportPlanner.Next(run, NovelIntegrationSnapshot.Build(run, fullIndex, completed), completed);
                if (plan.IsEmpty) break;
                plan = [.. plan.Select(n => n with { ExecutionConnection = run.Connection, ExecutionPreset = run.StageSettings?.For(n.Kind) })];
                var remaining = plan[0].Kind is AnalysisNodeKind.Integration or AnalysisNodeKind.Summary ? Math.Min(7, run.ReportReserve.Requests) : plan[0].Kind == AnalysisNodeKind.Dimension ? 1 : 0;
                if (run.Nodes.Length + plan.Length > 1000 || requests.List(run.Budget.Id).Count + plan.Length + remaining > run.Budget.MaximumRequests)
                    throw new InvalidOperationException("下一阶段节点超过剩余额度，请复核预算后继续。");
                var next = run with { Nodes = run.Nodes.AddRange(plan), Version = run.Version + 1, State = AnalysisRunState.Running, Message = "下一阶段分析计划已保存。", UpdatedAt = DateTimeOffset.UtcNow };
                runs.Save(next, run.Version); run = next; Notify(progress, run);
            }
            return run.Target switch
            {
                AnalysisTarget.Extraction => SaveState(run, AnalysisRunState.ExtractionCompleted, "全文单元提取完成；全局整合与综合报告尚未完成。", progress),
                AnalysisTarget.Integration => SaveState(run, AnalysisRunState.IntegrationCompleted, "全书信息整合完成；专题与综合报告尚未完成。", progress),
                _ => SaveState(run, AnalysisRunState.Completed, "全文分析报告候选已完成；语义质量待人工评阅。", progress)
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 不持有失败前的陈旧版本覆盖检查点；保存失败可再次抛出，磁盘已有 Running/Completed 可在重开时核对。
            run = runs.Read(runId);
            return SaveState(run, ct.IsCancellationRequested ? AnalysisRunState.Cancelled : AnalysisRunState.NeedsAttention,
                error is ModelRequestException ? error.Message : "分析已停止，请检查预算、连接版本、来源与本地检查点；已保存内容保留。", progress);
        }
    }

    /// <summary>明确接受原请求可能已经计费，才生成新的操作 ID；不清除或降低旧账本计费。</summary>
    public AnalysisRun Resume(Guid runId, bool acknowledgeUncertain, int maximumRequests, long maximumTokens, string reviewGuidance = "")
    {
        var run = runs.Read(runId); using var lease = runs.Acquire(run.Budget.Id); run = runs.Read(runId);
        if (run.State is AnalysisRunState.Running or AnalysisRunState.Completed or AnalysisRunState.ExtractionCompleted or AnalysisRunState.IntegrationCompleted) throw new InvalidOperationException("当前状态不允许修改恢复参数。");
        var budget = run.Budget with { MaximumRequests = maximumRequests, MaximumTokens = maximumTokens };
        var entries = requests.List(run.Budget.Id);
        if (entries.Any(e => (e.State != RequestState.Completed || e.Usage.InputTokens is null || e.Usage.OutputTokens is null) && !e.RetryAcknowledged) && !acknowledgeUncertain)
            throw new InvalidOperationException("存在需复核的请求；确认额外成本后才能重试。");
        var nodes = run.Nodes.Select(node => node.State != AnalysisNodeState.Completed && entries.Any(e => e.Id == node.OperationId && e.State != RequestState.Completed)
            ? node with { OperationId = Guid.NewGuid(), State = AnalysisNodeState.Pending, InputStamp = "", ReviewGuidance = string.IsNullOrWhiteSpace(reviewGuidance) ? node.ReviewGuidance : reviewGuidance } : node).ToImmutableArray();
        var next = run with { Budget = budget, Nodes = nodes, AllowFormatRetry = true, State = AnalysisRunState.Queued, Message = "已准备恢复；旧费用继续计入总额。", Version = run.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
        next.Validate(); requests.ReviseBudget(budget, acknowledgeUncertain); runs.Save(next, run.Version); return next;
    }

    /// <summary>
    /// 明确复核后重新校验已有候选，不发送网络请求。只接受已返回已知用量、随后被本地协议/契约拒绝的完整 JSON。
    /// 截断、网络中断、缺失用量和输入指纹改变均不可采纳；原失败账本及成本保持原样，仅标记已复核。
    /// </summary>
    public AnalysisRun AdoptReviewedCandidate(Guid runId)
    {
        var run = runs.Read(runId); using var lease = runs.Acquire(run.Budget.Id); run = runs.Read(runId);
        if (run.State != AnalysisRunState.NeedsAttention) throw new InvalidOperationException("当前没有待复核候选。");
        var position = Enumerable.Range(0, run.Nodes.Length).First(i => run.Nodes[i].State != AnalysisNodeState.Completed); var node = run.Nodes[position];
        var entries = requests.List(run.Budget.Id); var entry = entries.SingleOrDefault(e => e.Id == node.OperationId);
        if (entry is null || entry.State != RequestState.Uncertain || entry.Failure != ModelFailure.Protocol.ToString() || entry.Usage.InputTokens is null || entry.Usage.OutputTokens is null ||
            entry.BookId != run.BookId || entry.ConnectionId != run.Connection.Connection.Id || entry.ConnectionVersion != run.Connection.Connection.Version ||
            entries.Any(e => e.Id != node.OperationId && !e.RetryAcknowledged && (e.State != RequestState.Completed || e.Usage.InputTokens is null || e.Usage.OutputTokens is null)))
            throw new InvalidOperationException("候选不是已知用量的本地校验失败，或仍有其他未复核请求。");
        var source = sources.Read(run.BookId) with { Chunks = run.Chunks }; source.Validate();
        var results = run.Nodes.Where(n => n.State == AnalysisNodeState.Completed).ToDictionary(n => n.Key, n => runs.ReadResult(runId, n.Key) ?? throw new InvalidDataException("前置节点结果缺失。"));
        var prepared = preparer.Prepare(run, source, node, results);
        if (prepared.InputStamp != node.InputStamp) throw new InvalidDataException("候选输入已变化，不能在新提示下冒充原任务结果。");
        var json = ModelRequestService.RepairJsonWrapper(entry.PartialText); ModelRequestService.ValidateJson(json, prepared.Request.Contract);
        requests.ReviseBudget(run.Budget, true);
        var accepted = SaveNode(run, position, node with { State = AnalysisNodeState.Completed }, new(node.Key, node.InputStamp, json, AnalysisLimits.HashText(json)), null);
        return SaveState(accepted, AnalysisRunState.Paused, "已重新校验并采纳保存的候选，未产生新请求；可继续后续节点。", null);
    }

    public IReadOnlyList<ChunkAnalysisResult> ReadExtractions(Guid runId)
    {
        var run = runs.Read(runId); var input = sources.Read(run.BookId) with { Chunks = run.Chunks }; input.Validate(); var results = new List<ChunkAnalysisResult>();
        foreach (var node in run.Nodes.Where(n => n.Kind == AnalysisNodeKind.Extraction && n.State == AnalysisNodeState.Completed))
        {
            var chunk = input.Chunks.Single(c => c.Id == node.ChunkId);
            var result = runs.ReadResult(runId, node.Key) ?? throw new InvalidDataException("已完成节点缺少结果。");
            // 离线阅读校验保存时的指纹及原文，不拿新版提示覆盖旧报告的版本身份；执行/缓存复用仍严格重算当前指纹。
            if (result.InputStamp != node.InputStamp || AnalysisLimits.HashText(result.Json) != result.Hash) throw new InvalidDataException("已保存结果与节点指纹不一致。");
            var contract = new ChunkAnalysisContract(input.Source, chunk, node.OperationId, node.InputStamp, ChunkAnalysisContract.Passages(input.Source, chunk));
            ModelRequestService.ValidateJson(result.Json, contract); results.Add(contract.Read(result.Json));
        }
        return results;
    }

    private void CheckStageBudget(AnalysisRun run, AnalysisNodeKind kind, TextModelRequest request)
    {
        var entries = requests.List(run.Budget.Id); var reserve = kind switch
        {
            AnalysisNodeKind.Extraction => run.ReportReserve,
            AnalysisNodeKind.Integration => new(Math.Min(7, run.ReportReserve.Requests), Math.Min(400000, run.ReportReserve.Tokens)),
            AnalysisNodeKind.Summary => new(Math.Min(7, run.ReportReserve.Requests), Math.Min(400000, run.ReportReserve.Tokens)),
            AnalysisNodeKind.Dimension => new(Math.Min(1, run.ReportReserve.Requests), Math.Min(80000, run.ReportReserve.Tokens)),
            _ => new(0, 0)
        };
        if (entries.Count + 1 + reserve.Requests > run.Budget.MaximumRequests || entries.Sum(e => e.ChargedTokens) + ModelRequestService.EstimateReservation(request) + reserve.Tokens > run.Budget.MaximumTokens)
            throw new InvalidOperationException("当前阶段额度不足，后续整合和报告的预留不能被提取耗尽。");
    }

    /// <summary>
    /// 只有完整且用量已知的格式错误才允许一次定向纠正。先检查共享预算和所有旧请求，
    /// 再持久化新的操作 ID；重启不会清零次数，也不会顺便确认其他未知费用。
    /// </summary>
    private AnalysisRun? TryScheduleCorrection(AnalysisRun run, int index, PreparedAnalysisNode prepared, CancellationToken ct, IProgress<AnalysisRun>? progress)
    {
        var node = run.Nodes[index]; var entries = requests.List(run.Budget.Id);
        var entry = entries.SingleOrDefault(e => e.Id == node.OperationId);
        if (ct.IsCancellationRequested || !run.AllowFormatRetry || node.FormatRetries >= 1 ||
            entry is not { ResponseComplete: true, Diagnostic.CanCorrect: true, Usage.InputTokens: not null, Usage.OutputTokens: not null } ||
            entries.Any(e => e.Id != entry.Id && !e.RetryAcknowledged && (e.State != RequestState.Completed || e.Usage.InputTokens is null || e.Usage.OutputTokens is null))) return null;
        var guidance = node.ReviewGuidance + "\n本次只纠正当前单元：" + entry.Diagnostic.Message +
            "只返回一个符合契约的业务JSON对象，不回显Schema或Markdown，不添加未声明字段；保持依据，不补造事实。";
        if (guidance.Length > 2000) return null;
        var retry = node with
        {
            OperationId = Guid.NewGuid(),
            InputStamp = "",
            State = AnalysisNodeState.Pending,
            FormatRetries = node.FormatRetries + 1,
            ReviewGuidance = guidance
        };
        // 补充说明也占输入容量；此处以实际新提示核对预留，发送前账本还会再次检查。
        var revised = prepared.Request with { SystemPrompt = prepared.Request.SystemPrompt + guidance };
        try { CheckStageBudget(run, node.Kind, revised); }
        catch (InvalidOperationException) { return null; }
        requests.ReviseBudget(run.Budget, true);
        return SaveNode(run, index, retry, null, progress);
    }
    private static void Validate(AnalysisNodeResult result, PreparedAnalysisNode prepared)
    {
        if (result.InputStamp != prepared.InputStamp || AnalysisLimits.HashText(result.Json) != result.Hash) throw new InvalidDataException("缓存结果指纹不一致。");
        ModelRequestService.ValidateJson(result.Json, prepared.Request.Contract);
    }
    private AnalysisRun SaveNode(AnalysisRun run, int index, AnalysisNode node, AnalysisNodeResult? result, IProgress<AnalysisRun>? progress)
    {
        var next = run with { Nodes = run.Nodes.SetItem(index, node), State = AnalysisRunState.Running, Message = $"已完成 {run.Nodes.SetItem(index, node).Count(n => n.State == AnalysisNodeState.Completed)}/{run.Nodes.Length} 个节点。", Version = run.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
        runs.Save(next, run.Version, result); Notify(progress, next); return next;
    }
    private AnalysisRun SaveState(AnalysisRun run, AnalysisRunState state, string message, IProgress<AnalysisRun>? progress)
    {
        var next = run with { State = state, Message = message, Version = run.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
        runs.Save(next, run.Version); Notify(progress, next); return next;
    }
    private static void Notify(IProgress<AnalysisRun>? progress, AnalysisRun run)
    { try { progress?.Report(run); } catch (Exception error) when (error is not OutOfMemoryException) { } }
}
