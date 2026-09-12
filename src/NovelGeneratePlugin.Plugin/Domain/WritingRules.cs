using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
namespace NovelGeneratePlugin.Domain;

public enum WritingRuleKind { ForbiddenText, RequiredText, Guidance }
public enum WritingRuleScope { CommonSnapshot, Book, Volume, Run }
public enum WritingRuleStrength { Hard, Advisory }
public sealed record WritingRule(Guid Id, long Version, string Original, string Interpretation, WritingRuleKind Kind,
    WritingRuleScope Scope, Guid? ScopeId, WritingRuleStrength Strength, string Pattern, bool IgnoreSeparators,
    ImmutableArray<string> Exceptions, string Source, bool Enabled);

/// <summary>未提交的规则表单也属于作品编辑数据；关闭/重启不会丢失作者尚未整理好的规则原文。</summary>
public sealed record RuleDraft(Guid? EditingId, string Original, string Interpretation, WritingRuleKind Kind, WritingRuleScope Scope,
    WritingRuleStrength Strength, string Pattern, bool IgnoreSeparators, string ExceptionsText, string Source, bool Enabled)
{
    public static RuleDraft Empty { get; } = new(null, "", "", WritingRuleKind.ForbiddenText, WritingRuleScope.Book, WritingRuleStrength.Hard, "", false, "", "作者输入", true);
    public static RuleDraft From(WritingRule rule) => new(rule.Id, rule.Original, rule.Interpretation, rule.Kind, rule.Scope, rule.Strength, rule.Pattern,
        rule.IgnoreSeparators, string.Join('\n', rule.Exceptions), rule.Source, rule.Enabled);
}
public sealed record WritingRuleSet(long Version, ImmutableArray<WritingRule> Items)
{
    public static WritingRuleSet Empty { get; } = new(0, []);
    public void Validate(BookProject book)
    {
        if (Version < 0 || Items.IsDefault || Items.Length > 2000) throw new InvalidDataException("规则集版本或数量无效。");
        var ids = new HashSet<Guid>();
        foreach (var rule in Items)
        {
            if (rule is null || rule.Id == Guid.Empty || !ids.Add(rule.Id) || rule.Version < 1 || string.IsNullOrWhiteSpace(rule.Original) || rule.Original.Length > 10000 ||
                rule.Interpretation is null || rule.Source is null || rule.Pattern is null || rule.Pattern.Length > 2000 || !Enum.IsDefined(rule.Kind) || !Enum.IsDefined(rule.Scope) ||
                !Enum.IsDefined(rule.Strength) || rule.Exceptions.IsDefault || rule.Exceptions.Length > 50 || rule.Exceptions.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 2000))
                throw new InvalidDataException("规则身份、原文、范围或执行内容无效。");
            if (rule.Kind != WritingRuleKind.Guidance && string.IsNullOrWhiteSpace(RuleEvaluation.Normalize(rule.Pattern, rule.IgnoreSeparators).Text))
                throw new InvalidDataException("文本检测规则需要有效的匹配文本。");
            if (rule.Kind != WritingRuleKind.ForbiddenText && rule.Exceptions.Length != 0) throw new InvalidDataException("例外文本仅用于禁止文本规则，且必须完整覆盖命中片段。");
            if (rule.Scope == WritingRuleScope.Volume && !book.Volumes.Any(v => v.Id == rule.ScopeId) || rule.Scope == WritingRuleScope.Run && (rule.ScopeId is null || rule.ScopeId == Guid.Empty) ||
                rule.Scope is WritingRuleScope.Book or WritingRuleScope.CommonSnapshot && rule.ScopeId is not null)
                throw new InvalidDataException("规则范围目标无效。");
        }
    }
    public static BookProject Commit(BookProject book, RuleDraft draft, Guid chapterId)
    {
        var chapter = book.Chapters.Single(c => c.Id == chapterId);
        var previous = draft.EditingId is Guid id ? book.Rules.Items.Single(r => r.Id == id) : null;
        Guid? scopeId = previous is not null && previous.Scope == draft.Scope ? previous.ScopeId : draft.Scope switch
        {
            WritingRuleScope.Volume => chapter.VolumeId,
            WritingRuleScope.Run => book.Revisions.ActiveRunId ?? throw new InvalidOperationException("当前没有活动运行，不能建立本次运行规则。"),
            _ => null
        };
        var rule = new WritingRule(previous?.Id ?? Guid.NewGuid(), checked((previous?.Version ?? 0) + 1), draft.Original.Trim(), draft.Interpretation,
            draft.Kind, draft.Scope, scopeId, draft.Strength, draft.Pattern, draft.IgnoreSeparators,
            draft.ExceptionsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToImmutableArray(), draft.Source, draft.Enabled);
        var items = previous is null ? book.Rules.Items.Add(rule) : book.Rules.Items.Replace(previous, rule);
        var changed = book with { Rules = new WritingRuleSet(checked(book.Rules.Version + 1), items), RuleEditor = RuleDraft.From(rule) };
        changed.Validate(); return changed;
    }
}
public sealed record RuleFinding(Guid RuleId, long RuleVersion, WritingRuleStrength Strength, string Message, int Start, int Length, string Evidence);
public sealed record RuleConflict(Guid FirstRuleId, Guid SecondRuleId, string Message);
public sealed record LocalRuleCheck(Guid ChapterId, Guid? RunId, string TextHash, string RulesStamp, DateTimeOffset CheckedAt,
    ImmutableArray<RuleFinding> Findings, ImmutableArray<RuleConflict> Conflicts, int GuidanceCount)
{
    public bool Complete { get; init; } = true;
    public bool HasHardFailure => !Complete || Conflicts.Length > 0 || Findings.Any(f => f.Strength == WritingRuleStrength.Hard);
}

/// <summary>纯文本检测，不执行用户或模型提供的正则/代码。规范化同时保留 UTF-16 原文索引，方便编辑器定位。</summary>
public static class RuleEvaluation
{
    public static ImmutableArray<WritingRule> Applicable(BookProject book, Guid chapterId, Guid? runId)
    {
        var chapter = book.Chapters.Single(c => c.Id == chapterId);
        return book.Rules.Items.Where(r => r.Enabled && (r.Scope is WritingRuleScope.CommonSnapshot or WritingRuleScope.Book ||
            r.Scope == WritingRuleScope.Volume && r.ScopeId == chapter.VolumeId || r.Scope == WritingRuleScope.Run && runId is not null && r.ScopeId == runId)).ToImmutableArray();
    }
    public static string Stamp(BookProject book, Guid chapterId, Guid? runId) => RevisionRules.Hash(JsonSerializer.Serialize(new
    { book.Rules.Version, LegacyRules = book.Profile.Rules, Rules = Applicable(book, chapterId, runId) }));
    public static bool IsCurrent(BookProject book, LocalRuleCheck check) => book.Chapters.Any(c => c.Id == check.ChapterId && RevisionRules.Hash(c.Text) == check.TextHash) &&
        Stamp(book, check.ChapterId, check.RunId) == check.RulesStamp && check.RunId == book.Revisions.ActiveRunId;
    public static LocalRuleCheck Check(BookProject book, Guid chapterId, Guid? runId, string? selectedText = null)
    {
        book.Validate(); var text = selectedText ?? book.Chapters.Single(c => c.Id == chapterId).Text;
        var rules = Applicable(book, chapterId, runId); var findings = ImmutableArray.CreateBuilder<RuleFinding>(); var complete = true;
        foreach (var rule in rules.Where(r => r.Kind != WritingRuleKind.Guidance))
        {
            if (findings.Count >= 1000) { complete = false; break; }
            var normalized = Normalize(text, rule.IgnoreSeparators); var pattern = Normalize(rule.Pattern, rule.IgnoreSeparators).Text;
            if (rule.Kind == WritingRuleKind.RequiredText)
            {
                if (!normalized.Text.Contains(pattern, StringComparison.Ordinal)) findings.Add(new RuleFinding(rule.Id, rule.Version, rule.Strength, "缺少要求文本", -1, 0, rule.Pattern));
                continue;
            }
            var exceptions = rule.Exceptions.SelectMany(e => Occurrences(normalized.Text, Normalize(e, rule.IgnoreSeparators).Text)).Take(1001).ToArray();
            if (exceptions.Length > 1000) { complete = false; break; }
            foreach (var hit in Occurrences(normalized.Text, pattern))
            {
                if (exceptions.Any(e => e.Start <= hit.Start && e.Start + e.Length >= hit.Start + hit.Length)) continue;
                if (findings.Count >= 1000) { complete = false; break; }
                var start = normalized.Indices[hit.Start]; var end = normalized.Indices[hit.Start + hit.Length - 1] + 1;
                findings.Add(new RuleFinding(rule.Id, rule.Version, rule.Strength, "命中禁止文本", start, end - start, text[start..end]));
            }
            if (!complete) break;
        }
        var conflicts = ImmutableArray.CreateBuilder<RuleConflict>();
        foreach (var required in rules.Where(r => r.Strength == WritingRuleStrength.Hard && r.Kind == WritingRuleKind.RequiredText))
        {
            foreach (var forbidden in rules.Where(r => r.Strength == WritingRuleStrength.Hard && r.Kind == WritingRuleKind.ForbiddenText && r.Exceptions.IsEmpty))
                if (required.IgnoreSeparators == forbidden.IgnoreSeparators && Normalize(required.Pattern, required.IgnoreSeparators).Text.Contains(Normalize(forbidden.Pattern, forbidden.IgnoreSeparators).Text, StringComparison.Ordinal))
                {
                    if (conflicts.Count >= 1000) { complete = false; break; }
                    conflicts.Add(new RuleConflict(required.Id, forbidden.Id, "硬规则同时要求出现并禁止同一文本，请先调整规则。"));
                }
            if (conflicts.Count >= 1000) { complete = false; break; }
        }
        return new LocalRuleCheck(chapterId, runId, RevisionRules.Hash(text), Stamp(book, chapterId, runId), DateTimeOffset.UtcNow,
            findings.ToImmutable(), conflicts.ToImmutable(), rules.Count(r => r.Kind == WritingRuleKind.Guidance) + (string.IsNullOrWhiteSpace(book.Profile.Rules) ? 0 : 1))
        { Complete = complete };
    }
    private static IEnumerable<(int Start, int Length)> Occurrences(string text, string pattern)
    {
        if (pattern.Length == 0) yield break;
        for (var offset = 0; offset <= text.Length - pattern.Length;)
        {
            var index = text.IndexOf(pattern, offset, StringComparison.Ordinal); if (index < 0) yield break;
            yield return (index, pattern.Length); offset = index + 1;
        }
    }
    internal static (string Text, int[] Indices) Normalize(string text, bool ignoreSeparators)
    {
        var result = new StringBuilder(); var indices = new List<int>();
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index]; if (ignoreSeparators && (char.IsWhiteSpace(value) || char.IsPunctuation(value))) continue;
            result.Append(value); indices.Add(index);
        }
        return (result.ToString(), indices.ToArray());
    }
}
