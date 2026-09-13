using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
namespace NovelGeneratePlugin.Application.Models;

public enum RequestState { Reserved, Running, Completed, Truncated, Uncertain, Rejected }
public sealed record RequestBudget(Guid Id, int MaximumRequests, long MaximumTokens);
/// <summary>用量账本也是部分候选的恢复入口，但不保存提示词、推理或凭据。它不代表工作稿或正式稿。</summary>
public sealed record ModelRequestEntry(Guid Id, Guid BudgetId, Guid BookId, Guid ConnectionId, long ConnectionVersion,
    string Model, long ReservedTokens, RequestState State, ModelUsage Usage, string PartialText, string? Failure, DateTimeOffset UpdatedAt)
{
    public bool RetryAcknowledged { get; init; }
    public ModelDiagnostic? Diagnostic { get; init; }
    public bool ResponseComplete { get; init; }
    public long ChargedTokens => Usage.InputTokens is long input && Usage.OutputTokens is long output ? checked(input + output) : ReservedTokens;
    public override string ToString() => $"{UpdatedAt.LocalDateTime:MM-dd HH:mm:ss} · {Model} · {State switch { RequestState.Completed => "完成", RequestState.Truncated => "截断", RequestState.Rejected => "拒绝", RequestState.Uncertain => "结果不确定", _ => "未确认结束" }}";
}
public interface IModelRequestStore
{
    void Reserve(ModelRequestEntry entry, RequestBudget budget);
    void Save(ModelRequestEntry entry);
    void ReviseBudget(RequestBudget budget, bool acknowledgeUncertain);
    IReadOnlyList<ModelRequestEntry> List(Guid budgetId);
    IReadOnlyList<ModelRequestEntry> Recent(Guid connectionId);
}

/// <summary>
/// 所有付费请求从这里进入：先持久化预留，再调用窄适配器。一次操作只发送一次；网络断开可能已计费，
/// 因此不使用通用重试器。后续运行调度必须明确创建新操作并复用同一预算，不能把不确定结果当成零成本。
/// </summary>
public sealed class ModelRequestService(ITextModel model, IModelRequestStore store)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ConnectionGates = new();
    public void ReviseBudget(RequestBudget budget, bool acknowledgeUncertain) => store.ReviseBudget(budget, acknowledgeUncertain);
    public IReadOnlyList<ModelRequestEntry> List(Guid budgetId) => store.List(budgetId);
    public Task<IReadOnlyList<ModelRequestEntry>> RecentAsync(Guid connectionId) => Task.Run(() => store.Recent(connectionId));
    public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, RequestBudget budget,
        IProgress<string>? progress, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        request.Validate();
        // 同一进程的同一连接默认一个在途请求；排队等待不预留、不计为已发送。不同作品不会并发挤占同一连接。
        var gate = ConnectionGates.GetOrAdd(request.Configuration.Connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await GenerateCoreAsync(request, budget, progress, cancellationToken, timeout).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<TextModelResponse> GenerateCoreAsync(TextModelRequest request, RequestBudget budget,
        IProgress<string>? progress, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        request.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var duration = timeout ?? TimeSpan.FromMinutes(5);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(15)) throw new ArgumentOutOfRangeException(nameof(timeout));
        // UTF-8 字节数用于保守输入预留，不冒充服务商 tokenizer。Codex 还包含 CLI 固定上下文，留出独立余量。
        var reserved = EstimateReservation(request);
        var entry = new ModelRequestEntry(request.OperationId, budget.Id, request.Configuration.BookId, request.Configuration.Connection.Id,
            request.Configuration.Connection.Version, request.Configuration.Preset.Model, reserved, RequestState.Reserved,
            new(null, null), "", null, DateTimeOffset.UtcNow);
        await Task.Run(() => store.Reserve(entry, budget), cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(duration);
        var gate = new object(); var lastNotification = DateTimeOffset.MinValue;
        // 适配器顺序报告累计正文。同步保存保证先落盘、后显示；UI 通知最多每 150ms 一次，最终值必发。
        var sink = new InlineProgress(text =>
        {
            lock (gate)
            {
                if (text.Length > 1000000) throw new ModelRequestException(ModelFailure.Protocol, "模型正文超过本地容量限制。");
                entry = entry with { PartialText = text, State = RequestState.Running, UpdatedAt = DateTimeOffset.UtcNow };
                store.Save(entry);
                if (entry.UpdatedAt - lastNotification >= TimeSpan.FromMilliseconds(150)) { Notify(progress, text); lastNotification = entry.UpdatedAt; }
            }
        });
        try
        {
            var response = await model.GenerateAsync(request, sink, deadline.Token).ConfigureAwait(false);
            sink.Report(response.Text);
            if (response.Usage.InputTokens is < 0 || response.Usage.OutputTokens is < 0) throw new ModelRequestException(ModelFailure.Protocol, "模型用量计数无效。");
            entry = entry with { Usage = response.Usage, ResponseComplete = response.Completion == ModelCompletion.Complete, Diagnostic = response.Diagnostic };
            // 先保存已获得的用量，再验证业务正文；思考耗尽预算的空截断仍是有成本的已知截断。
            if (!Enum.IsDefined(response.Completion) || response.Text is null ||
                response.Completion == ModelCompletion.Complete && string.IsNullOrWhiteSpace(response.Text))
                throw new ModelRequestException(ModelFailure.Protocol, "模型未返回有效正文。");
            if (response.Completion == ModelCompletion.Complete && request.JsonOutput)
            {
                if (request.AllowJsonWrapperRepair) response = response with { Text = RepairJsonWrapper(response.Text) };
                ValidateJson(response.Text, request.Contract);
            }
            entry = entry with { State = response.Completion == ModelCompletion.Complete ? RequestState.Completed : RequestState.Truncated };
            store.Save(entry); Notify(progress, response.Text); return response;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failure = exception switch
            {
                OperationCanceledException => cancellationToken.IsCancellationRequested ? ModelFailure.Cancelled : ModelFailure.Timeout,
                ModelRequestException known => known.Failure,
                _ => ModelFailure.Local
            };
            var diagnostic = (exception as ModelRequestException)?.Diagnostic ??
                (failure is ModelFailure.Timeout or ModelFailure.Cancelled
                    ? new ModelDiagnostic(failure == ModelFailure.Timeout ? ModelDiagnosticCode.Timeout : ModelDiagnosticCode.Cancelled) : null);
            var observed = (exception as ModelRequestException)?.ObservedUsage ?? (exception as ModelStreamCancellationException)?.ObservedUsage;
            entry = entry with
            {
                State = failure is ModelFailure.Authentication or ModelFailure.Balance or ModelFailure.RateLimit ? RequestState.Rejected : RequestState.Uncertain,
                Failure = failure.ToString(),
                Diagnostic = diagnostic is null ? entry.Diagnostic : diagnostic with { ResponseComplete = entry.ResponseComplete, OutputCharacters = entry.PartialText.Length },
                Usage = observed ?? entry.Usage,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            // 即使最终写入失败，之前的 Reserved/Running 和片段仍在；下一次同预算请求会被保守阻止。
            try { store.Save(entry); } catch (Exception writeError) when (writeError is not OutOfMemoryException) { throw new ModelRequestException(ModelFailure.Local, "请求已停止，但最终账本写入失败；请保留当前窗口并检查本地存储。已有片段仍可恢复。"); }
            throw new ModelRequestException(failure, entry.Diagnostic?.Message ?? (failure switch
            {
                ModelFailure.Cancelled => "请求已取消；已有候选保留，远端用量可能尚未确认。",
                ModelFailure.Timeout => "请求超时；已有候选保留，未自动重发。",
                ModelFailure.Authentication => "模型认证失败，请检查此连接的登录或密钥。",
                ModelFailure.Balance => "模型余额不足，已暂停请求。",
                ModelFailure.RateLimit => "模型请求受到限流，未自动重试。",
                ModelFailure.Protocol => "模型响应不符合协议或结构要求，已保留候选供复核。",
                _ => "模型请求未完成，已保留可用候选；请检查连接及本地存储。"
            }))
            { Diagnostic = entry.Diagnostic, ObservedUsage = entry.Usage };
        }
    }
    /// <summary>供运行调度预留后续阶段额度；真正发送仍在账本事务中重新核验，预估不能替代付款边界。</summary>
    public static long EstimateReservation(TextModelRequest request) => Encoding.UTF8.GetByteCount(request.SystemPrompt) +
        Encoding.UTF8.GetByteCount(request.UserPrompt) + Encoding.UTF8.GetByteCount(request.Contract?.JsonSchema ?? "") +
        (long)request.Configuration.Preset.MaxOutputTokens + 16384;
    /// <summary>仅去掉 BOM、外围空白和完整的单层 JSON 代码围栏，不补字段、不猜引号、不修补截断内容；原始响应仍在请求账本。</summary>
    public static string RepairJsonWrapper(string text)
    {
        var value = text.Trim().TrimStart('\uFEFF').Trim();
        var normalized = value.Replace("\r\n", "\n");
        var prefix = normalized.StartsWith("```json\n", StringComparison.Ordinal) ? 8 : normalized.StartsWith("```\n", StringComparison.Ordinal) ? 4 : 0;
        return prefix > 0 && normalized.EndsWith("\n```", StringComparison.Ordinal) ? normalized[prefix..^4].Trim() : value;
    }
    public static void ValidateJson(string text, IModelOutputContract? contract)
    {
        try
        {
            if (contract is not null && ModelJsonDiagnostics.IsSchemaEcho(text))
                throw new ModelRequestException(ModelFailure.Protocol, "模型回显了格式定义。") { Diagnostic = new(ModelDiagnosticCode.SchemaEcho) };
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.EnumerateObject().Any()) throw new JsonException();
            CheckDuplicates(json.RootElement); contract?.Validate(json.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            var path = ModelJsonDiagnostics.SafePath((error as JsonException)?.Path, contract?.JsonSchema);
            var diagnostic = new ModelDiagnostic(error is JsonException && string.IsNullOrEmpty((error as JsonException)?.Path)
                ? ModelDiagnosticCode.InvalidJson : ModelDiagnosticCode.ContractMismatch, path);
            throw new ModelRequestException(ModelFailure.Protocol, diagnostic.Message) { Diagnostic = diagnostic };
        }
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) CheckDuplicates(child);
    }
    private static void Notify(IProgress<string>? progress, string text)
    {
        // 显示端不是执行结果的所有者：显示失败不能把已经落盘的成功结果回写成不确定状态。
        try { progress?.Report(text); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
