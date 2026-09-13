using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Templates;

public sealed class TemplateConversionSaveException(string message, Exception inner) : IOException(message, inner);

/// <summary>
/// 固定流程的转换用例：冻结报告、发送有界请求、保存候选。网络取消不取消已取得结果的本地保存。
/// 远端调用无法保证恰好一次，恢复时先核对账本；本地通过稳定操作和检查点避免重复付费与重复采纳。
/// </summary>
public sealed class ReportTemplateConversionService(NovelAnalysisReportService reports, IAnalysisRunStore analysis,
    ConnectionService connections, ModelRequestService requests, IReportTemplateConversionStore store)
{
    public ReportTemplateConversion Read(Guid id) => store.Read(id);
    public IReadOnlyList<ReportTemplateConversion> List(Guid runId, int offset = 0) => store.List(runId, offset);
    public IReadOnlyList<ModelRequestEntry> Usage(Guid id) => requests.List(store.Read(id).Budget.Id);
    public async Task<ReportTemplateConversion> PrepareAsync(Guid runId, ConnectionBinding binding, string name,
        ProfileDimensions dimensions, int maximumRequests, long maximumTokens, ModelPreset? preset = null,
        long contextTokens = 131072, CancellationToken cancellationToken = default)
    {
        var source = await Task.Run(() => ReportTemplateInputBuilder.Build(reports.Read(runId), analysis.Read(runId).BookId, dimensions), cancellationToken).ConfigureAwait(false);
        var connection = await connections.FreezeAsync(binding, source.BookId, ModelTask.Checking).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var run = new ReportTemplateConversion(Guid.NewGuid(), 1, Guid.NewGuid(), source, name.Trim(), dimensions, connection,
            preset ?? connection.Preset with { MaxOutputTokens = Math.Min(connection.Preset.MaxOutputTokens, 8192) },
            contextTokens, new(Guid.NewGuid(), maximumRequests, maximumTokens), [], TemplateConversionState.Queued, null, "", now, now);
        // 先验证模型参数，防止输入计划把不支持的配置误认为只是容量不足。
        run.Preset.Validate();
        if (run.Connection.Connection.Settings.Provider == ModelProvider.CodexCli && run.Preset.ReasoningEffort is "none" or "max")
            throw new InvalidOperationException("Codex 转换请选择受支持的思考强度。");
        if (contextTokens is < 8192 or > 2000000) throw new ArgumentOutOfRangeException(nameof(contextTokens));
        run = run with { Steps = ReportTemplateRequests.Plan(run) }; run.Validate();
        if (run.Steps.Length > maximumRequests) throw new InvalidOperationException($"当前计划至少需要 {run.Steps.Length} 次请求，超过本次请求上限。");
        cancellationToken.ThrowIfCancellationRequested(); return run;
    }
    public Task CreateAsync(ReportTemplateConversion prepared) => Task.Run(() => store.Create(prepared));
    public Task<ReportTemplateConversion> ExecuteAsync(Guid id, IProgress<ReportTemplateConversion>? progress, CancellationToken cancellationToken,
        bool acknowledgeRetry = false, int? maximumRequests = null, long? maximumTokens = null) =>
        Task.Run(() => ExecuteCoreAsync(id, progress, cancellationToken, acknowledgeRetry, maximumRequests, maximumTokens));

    private async Task<ReportTemplateConversion> ExecuteCoreAsync(Guid id, IProgress<ReportTemplateConversion>? progress, CancellationToken cancellationToken,
        bool acknowledgeRetry, int? maximumRequests, long? maximumTokens)
    {
        using var lease = store.Acquire(id); var run = Recover(store.Read(id));
        if (run.State is TemplateConversionState.CandidateSaved or TemplateConversionState.DraftSaved) return run;
        try
        {
            var budget = run.Budget with { MaximumRequests = maximumRequests ?? run.Budget.MaximumRequests, MaximumTokens = maximumTokens ?? run.Budget.MaximumTokens };
            if (budget.MaximumRequests < run.Budget.MaximumRequests || budget.MaximumTokens < run.Budget.MaximumTokens)
                throw new InvalidOperationException("继续转换不能降低已建立预算。");
            if (budget != run.Budget || acknowledgeRetry)
            {
                requests.ReviseBudget(budget, acknowledgeRetry);
                run = Persist(run, run with { Budget = budget });
            }
            if (acknowledgeRetry)
            {
                var pending = run.Steps.FirstOrDefault(s => s.State != TemplateConversionStepState.Completed);
                if (pending is not null)
                {
                    var entry = requests.List(run.Budget.Id).SingleOrDefault(e => e.Id == pending.OperationId);
                    // 完整且有效的已有响应优先本地采纳，不能把“继续”一律实现为新发请求。
                    if (entry is not null && !CanRestore(run, pending, entry))
                        run = Persist(run, run with
                        {
                            Steps = run.Steps.SetItem(run.Steps.IndexOf(pending),
                            pending with { OperationId = Guid.NewGuid(), InputStamp = "", State = TemplateConversionStepState.Pending })
                        });
                }
            }
            run = Persist(run, run with { State = TemplateConversionState.Running, Message = "正在转换已保存报告。" }); Notify(progress, run);
            while (run.Steps.FirstOrDefault(s => s.State != TemplateConversionStepState.Completed) is { } step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = ReportTemplateRequests.Prepare(run, step); request.Validate();
                var stamp = ReportTemplateRequests.Stamp(request, run.Source.Stamp);
                if (step.InputStamp != "" && step.InputStamp != stamp) throw new InvalidDataException("请求输入与冻结检查点不一致，未发送。");
                if (step.State == TemplateConversionStepState.Pending)
                {
                    step = step with { State = TemplateConversionStepState.Running, InputStamp = stamp };
                    run = Persist(run, run with { Steps = run.Steps.SetItem(run.Steps.IndexOf(run.Steps.Single(s => s.Key == step.Key)), step) });
                    Notify(progress, run);
                }
                try
                {
                    var entry = requests.List(run.Budget.Id).SingleOrDefault(e => e.Id == step.OperationId);
                    string json;
                    if (entry is not null)
                    {
                        if (entry.BookId != run.Source.BookId || entry.BudgetId != run.Budget.Id || entry.ConnectionId != run.Connection.Connection.Id ||
                            entry.ConnectionVersion != run.Connection.Connection.Version || entry.ExecutionPreset != run.Preset)
                            throw new InvalidDataException("请求账本与转换冻结身份不一致，不能采纳。");
                        if (!entry.ResponseComplete || entry.Usage.InputTokens is null || entry.Usage.OutputTokens is null)
                            throw new InvalidOperationException("该步骤已有未确认完整性或费用的请求；请先复核候选及用量，再明确继续。");
                        json = ModelRequestService.RepairJsonWrapper(entry.PartialText);
                        ModelRequestService.ValidateJson(json, request.Contract);
                        // 已知完整且通过当前冻结契约的响应可本地恢复。只确认本预算中已明确的失败，避免顺带确认未知费用。
                        if (entry.State != RequestState.Completed && !entry.RetryAcknowledged)
                        {
                            if (HasOtherUnreviewed(run, entry.Id)) throw new InvalidOperationException("还有其他未复核请求，不能继续发送。");
                            requests.ReviseBudget(run.Budget, true);
                        }
                    }
                    else
                    {
                        await connections.ValidateCurrentAsync(run.Connection).ConfigureAwait(false);
                        var response = await requests.GenerateAsync(request, run.Budget, null, cancellationToken).ConfigureAwait(false);
                        if (response.Completion != ModelCompletion.Complete || response.Usage.InputTokens is null || response.Usage.OutputTokens is null)
                            throw new InvalidOperationException("转换响应截断或用量未确认；候选已进入请求账本，请复核后继续。");
                        json = response.Text;
                    }
                    var contract = (ReportTemplateContract)request.Contract!;
                    json = ReportTemplateContract.Serialize(contract.Read(json));
                    var completed = step with { State = TemplateConversionStepState.Completed, ResultJson = json };
                    run = Persist(run, run with { Steps = run.Steps.SetItem(run.Steps.IndexOf(step), completed), Message = $"已保存步骤 {step.Key}。" });
                    Notify(progress, run);
                }
                catch (ModelRequestException error) when (error.Diagnostic is not null)
                {
                    var entry = requests.List(run.Budget.Id).SingleOrDefault(e => e.Id == step.OperationId);
                    if (step.Corrections != 0 || !KnownComplete(entry) || HasOtherUnreviewed(run, step.OperationId) ||
                        error.Diagnostic.Code is not (ModelDiagnosticCode.ContractMismatch or ModelDiagnosticCode.InvalidJson or ModelDiagnosticCode.SchemaEcho))
                        throw;
                    requests.ReviseBudget(run.Budget, true);
                    var corrected = step with
                    {
                        OperationId = Guid.NewGuid(),
                        State = TemplateConversionStepState.Pending,
                        InputStamp = "",
                        Corrections = 1,
                        Guidance = "只纠正以下格式或字段问题，不新增原著事实：" + error.Diagnostic.Message
                    };
                    run = Persist(run, run with { Steps = run.Steps.SetItem(run.Steps.IndexOf(step), corrected), Message = "已保存错误诊断，执行一次有界纠正。" });
                    Notify(progress, run);
                }
            }
            var candidate = ReportTemplateRequests.Assemble(run);
            run = Persist(run, run with { Candidate = candidate, State = TemplateConversionState.CandidateSaved, Message = "模板规范候选已保存，可交付为新草案。" });
            Notify(progress, run); return run;
        }
        catch (TemplateConversionSaveException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var cancelled = error is OperationCanceledException or ModelRequestException { Failure: ModelFailure.Cancelled };
            var message = cancelled ? "转换已取消；成功步骤与已有候选保留。" :
                error is ModelRequestException model ? model.Message : error is IOException ? "本地转换数据读取失败，请检查存储后恢复。" : error.Message;
            if (message.Length > 2000) message = message[..2000];
            run = Persist(run, run with { State = cancelled ? TemplateConversionState.Cancelled : TemplateConversionState.NeedsAttention, Message = message });
            Notify(progress, run); return run;
        }
    }
    private static bool KnownComplete(ModelRequestEntry? entry) => entry is { ResponseComplete: true, Usage.InputTokens: not null, Usage.OutputTokens: not null };
    private bool HasOtherUnreviewed(ReportTemplateConversion run, Guid id) =>
        requests.List(run.Budget.Id).Any(e => e.Id != id && !e.RetryAcknowledged && (e.State != RequestState.Completed || !KnownComplete(e)));
    private static bool CanRestore(ReportTemplateConversion run, TemplateConversionStep step, ModelRequestEntry entry)
    {
        if (!KnownComplete(entry)) return false;
        try { ModelRequestService.ValidateJson(ModelRequestService.RepairJsonWrapper(entry.PartialText), ReportTemplateRequests.Prepare(run, step).Contract); return true; }
        catch (Exception error) when (error is ModelRequestException or InvalidOperationException) { return false; }
    }
    private ReportTemplateConversion Recover(ReportTemplateConversion current)
    {
        var recovery = store.ReadRecovery(current.Id); if (recovery is null) return current;
        if (recovery.Revision > current.Revision)
        {
            if (recovery.Revision != current.Revision + 1) throw new InvalidDataException("转换恢复版本不连续，请保留恢复文件并复核。");
            store.Save(recovery, current.Revision); current = store.Read(current.Id);
        }
        store.DeleteRecovery(current.Id); return current;
    }
    private ReportTemplateConversion Persist(ReportTemplateConversion previous, ReportTemplateConversion next)
    {
        next = next with { Revision = previous.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        try { store.Save(next, previous.Revision); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 仅本地介质写入失败需要恢复文件；并发冲突不能把陈旧窗口写成待覆盖的恢复版本。
            if (error is InvalidOperationException or InvalidDataException) throw new TemplateConversionSaveException("检查点冲突或不符合冻结契约，未覆盖现有任务。", error);
            try { store.WriteRecovery(next); }
            catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
            { throw new TemplateConversionSaveException("检查点及独立恢复文件均保存失败；请保留当前窗口并检查存储，未声明保存成功。", error); }
            throw new TemplateConversionSaveException("检查点保存失败，已保留独立恢复文件；继续时先补保存，不重新调用 AI。", error);
        }
        return next;
    }
    private static void Notify(IProgress<ReportTemplateConversion>? progress, ReportTemplateConversion run)
    { try { progress?.Report(run); } catch (Exception error) when (error is not OutOfMemoryException) { /* 展示失败不回滚已保存候选。 */ } }
}
