using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Templates;

public enum TemplateRuleBasis { Supported, Inferred, Suggestion }
public sealed record ReportTemplateEvidence(int FactId, string Quote);
public sealed record ReportTemplateClaim(int Id, ProfileDimensions Targets, string Topic, string Text,
    TemplateRuleBasis Basis, ImmutableArray<ReportTemplateEvidence> Evidence);

/// <summary>冻结实际提供给转换的报告材料。引用编号属于这份快照，不能用另一份报告的同号结论替代。</summary>
public sealed record ReportTemplateSource(Guid BookId, Guid RunId, Guid SourceId, string TextHash, string ReportVersion,
    string Name, ImmutableArray<ReportTemplateClaim> Claims, ImmutableArray<string> Notes)
{
    public string Stamp => CanonicalJson.Hash(new { BookId, RunId, SourceId, TextHash, ReportVersion, Name, Claims, Notes });
    public void Validate()
    {
        if (BookId == Guid.Empty || RunId == Guid.Empty || SourceId == Guid.Empty || !ConversionLimits.Hash(TextHash) ||
            !ConversionLimits.Hash(ReportVersion) || string.IsNullOrWhiteSpace(Name) || Name.Length > 200 ||
            Claims.IsDefaultOrEmpty || Claims.Length > 256 || Claims.Any(c => c is null) ||
            Claims.Select(c => c.Id).Distinct().Count() != Claims.Length || Notes.IsDefault || Notes.Length > 128 ||
            Notes.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 600))
            throw new InvalidDataException("报告转换来源不完整或超出容量。");
        foreach (var claim in Claims)
            if (claim.Id < 1 || !ConversionLimits.Dimensions(claim.Targets) || !Enum.IsDefined(claim.Basis) ||
                string.IsNullOrWhiteSpace(claim.Topic) || claim.Topic.Length > 120 || string.IsNullOrWhiteSpace(claim.Text) ||
                claim.Text.Length > 2400 || claim.Evidence.IsDefault || claim.Evidence.Length > 2 ||
                claim.Evidence.Any(e => e is null || e.FactId < 1 || string.IsNullOrWhiteSpace(e.Quote) || e.Quote.Length > 400))
                throw new InvalidDataException("报告转换结论或来源证据无效。");
    }
}

public sealed record TemplateConversionRule(string Text, string Condition, TemplateRuleBasis Basis, ImmutableArray<int> Sources);
public sealed record TemplateConversionSection(ProfileDimensions Dimension, ImmutableArray<TemplateConversionRule> Rules, ImmutableArray<string> Questions);
public sealed record TemplateConversionCandidate(ImmutableArray<TemplateConversionSection> Sections);
public enum TemplateConversionState { Queued, Running, NeedsAttention, CandidateSaved, DraftSaved, Cancelled }
public enum TemplateConversionStepState { Pending, Running, Completed }
public enum TemplateConversionStepKind { Extract, Merge }
public sealed record TemplateConversionStep(string Key, TemplateConversionStepKind Kind, ProfileDimensions Dimensions,
    ImmutableArray<int> SourceIds, ImmutableArray<string> Dependencies, Guid OperationId,
    TemplateConversionStepState State, string InputStamp, string? ResultJson, int Corrections, string Guidance);

/// <summary>
/// 转换任务独立计费，先固化请求身份再发送。检查点保存有界结果而不是全书；完成候选后才交付模板，
/// 跨库失败通过固定 TemplateId 和来源凭据补偿，不让恢复动作覆盖用户后来的草案编辑。
/// </summary>
public sealed record ReportTemplateConversion(Guid Id, long Revision, Guid TemplateId, ReportTemplateSource Source,
    string Name, ProfileDimensions Dimensions, FrozenConnection Connection, ModelPreset Preset, long? ContextTokens,
    RequestBudget Budget, ImmutableArray<TemplateConversionStep> Steps, TemplateConversionState State,
    TemplateConversionCandidate? Candidate, string Message, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string ContractVersion { get; init; } = ConversionLimits.Version;
    public void Validate()
    {
        if (Source is null || Connection is null || Budget is null || Preset is null) throw new InvalidDataException("转换配置不能为空。");
        Source.Validate(); Connection.Connection.Validate(); Connection.Preset.Validate(); Preset.Validate();
        if (Id == Guid.Empty || TemplateId == Guid.Empty || Revision < 1 || ContractVersion != ConversionLimits.Version ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || !ConversionLimits.Dimensions(Dimensions) ||
            Connection.BookId != Source.BookId || Connection.Task != ModelTask.Checking || Connection.Preset != Connection.Connection.Settings.Checking ||
            ContextTokens is < 8192 or > 2000000 || Budget.Id == Guid.Empty || Budget.MaximumRequests is < 1 or > 1000 ||
            Budget.MaximumTokens < 1 || !Enum.IsDefined(State) || Message is null || Message.Length > 2000 ||
            UpdatedAt < CreatedAt || Steps.IsDefaultOrEmpty || Steps.Length > 64)
            throw new InvalidDataException("报告转模板任务的身份、配置或预算无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal); var operations = new HashSet<Guid>();
        var sources = Source.Claims.Select(c => c.Id).ToHashSet();
        foreach (var step in Steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Key) || step.Key.Length > 80 || !seen.Add(step.Key) ||
                !Enum.IsDefined(step.Kind) || !Enum.IsDefined(step.State) || !ConversionLimits.Dimensions(step.Dimensions) ||
                (step.Dimensions & Dimensions) != step.Dimensions || step.OperationId == Guid.Empty || !operations.Add(step.OperationId) ||
                step.SourceIds.IsDefaultOrEmpty || step.SourceIds.Any(id => !sources.Contains(id)) ||
                step.SourceIds.Distinct().Count() != step.SourceIds.Length || step.Dependencies.IsDefault ||
                step.Dependencies.Any(key => key == step.Key || !seen.Contains(key)) || step.Dependencies.Distinct().Count() != step.Dependencies.Length ||
                step.Kind == TemplateConversionStepKind.Extract && !step.Dependencies.IsEmpty ||
                step.Kind == TemplateConversionStepKind.Merge && step.Dependencies.IsEmpty ||
                step.Corrections is < 0 or > 1 || step.Guidance is null || step.Guidance.Length > 1000 ||
                step.InputStamp is null || step.InputStamp != "" && !ConversionLimits.Hash(step.InputStamp) ||
                step.State != TemplateConversionStepState.Pending && !ConversionLimits.Hash(step.InputStamp) ||
                step.State == TemplateConversionStepState.Completed && string.IsNullOrWhiteSpace(step.ResultJson) ||
                step.State != TemplateConversionStepState.Completed && step.ResultJson is not null)
                throw new InvalidDataException("转换子步骤的引用、身份或完成状态无效。");
            if (step.ResultJson is not null)
                new ReportTemplateContract(step.Dimensions, Source.Claims.Where(c => step.SourceIds.Contains(c.Id)).ToArray()).Read(step.ResultJson);
        }
        if (Candidate is not null)
        {
            new ReportTemplateContract(Dimensions, Source.Claims).ValidateCandidate(Candidate);
            if (Steps.Any(s => s.State != TemplateConversionStepState.Completed))
                throw new InvalidDataException("子步骤未完成，不能保存最终候选。");
        }
        if (State is TemplateConversionState.CandidateSaved or TemplateConversionState.DraftSaved && Candidate is null)
            throw new InvalidDataException("缺少候选，不能声明草案已交付。");
    }
}

public static class ConversionLimits
{
    public const string Version = "report-template-v1";
    public const int RulesPerSection = 16, RuleCharacters = 600, ConditionCharacters = 200;
    public const int SourcesPerRule = 4, QuestionsPerSection = 8, QuestionCharacters = 300, JsonCharacters = 80000;
    public static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    public static bool Dimensions(ProfileDimensions value) => value != ProfileDimensions.None && (value & ~ProfileDimensions.All) == 0;
    public static ProfileDimensions[] Selected(ProfileDimensions value) =>
        new[] { ProfileDimensions.World, ProfileDimensions.Style, ProfileDimensions.Methods, ProfileDimensions.Rules }.Where(d => value.HasFlag(d)).ToArray();
}

public interface IReportTemplateConversionStore
{
    IDisposable Acquire(Guid id);
    void Create(ReportTemplateConversion conversion);
    ReportTemplateConversion Read(Guid id);
    IReadOnlyList<ReportTemplateConversion> List(Guid runId, int offset = 0, int limit = 20);
    void Save(ReportTemplateConversion conversion, long expectedRevision);
    void WriteRecovery(ReportTemplateConversion conversion);
    ReportTemplateConversion? ReadRecovery(Guid id);
    void DeleteRecovery(Guid id);
}
