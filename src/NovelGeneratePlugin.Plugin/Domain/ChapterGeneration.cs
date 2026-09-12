using System.Collections.Immutable;
using System.Text;
namespace NovelGeneratePlugin.Domain;

public enum ChapterWorkState { Drafting, Reviewing, Repairing, Ready, NeedsAttention, Cancelled, Failed }
public enum ReviewSeverity { Hard, Uncertain, Advice }
public enum ReviewCategory { Rule, Fact, PlotMethod, Style }
public sealed record ChapterIssue(ReviewSeverity Severity, ReviewCategory Category, string Message, string Evidence)
{
    public int Locate(string text) => string.IsNullOrEmpty(Evidence) ? -1 : text.IndexOf(Evidence, StringComparison.Ordinal);
}
public sealed record ReviewedFact(Guid EntityId, StoryFactKind Kind, string Field, string? ExpectedValue, string? NewValue,
    string Evidence, StoryFactTime Time, Guid? RelatedEntityId);
public sealed record ChapterReview(bool RulesChecked, bool FactsChecked, bool PlotChecked, bool StyleChecked, bool LockedPlanPreserved,
    string Summary, ImmutableArray<ChapterIssue> Issues, ImmutableArray<ReviewedFact> Facts);
public sealed record ChapterPatch(string OldText, string NewText);
public sealed record ChapterPatches(ImmutableArray<ChapterPatch> Patches);
public sealed record ChapterAttempt(int Number, string Text, ChapterReview? Review, ImmutableArray<ChapterIssue> Issues);
/// <summary>单章候选是独立恢复记录，不是稿件指针。完整输入指纹与正文摘要共同阻止迟到结果覆盖作者的新编辑。</summary>
public sealed record ChapterWork(Guid Id, Guid BookId, Guid ChapterId, Guid RunId, string SourceStamp, string OriginalTextHash,
    int TargetCharacters, int MaximumRepairs, ChapterWorkState State, string Text, ChapterReview? Review,
    ImmutableArray<ChapterIssue> Issues, ImmutableArray<FactDelta> Facts, ImmutableArray<ChapterAttempt> Attempts, string Message)
{
    public long UpdateSequence { get; init; }
    public string SourceDescription { get; init; } = "整章生成";
    public int Characters => Text.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r));
    public void Validate()
    {
        if (Id == Guid.Empty || BookId == Guid.Empty || ChapterId == Guid.Empty || RunId == Guid.Empty || SourceStamp is null || OriginalTextHash is null ||
            TargetCharacters is < 100 or > 10000 || MaximumRepairs is < 0 or > 2 || !Enum.IsDefined(State) || Text is null || Text.Length > 1000000 ||
            Issues.IsDefault || Facts.IsDefault || Attempts.IsDefault || Attempts.Length > 3 || Message is null)
            throw new InvalidDataException("章节任务记录无效。");
    }
}

public static class ChapterGenerationRules
{
    public static void Preflight(BookProject book, Guid chapterId, Guid runId)
    {
        book.Validate(); PlanningRules.RequireReady(book, chapterId);
        if (runId == Guid.Empty || book.Revisions.ActiveRunId is Guid active && active != runId) throw new InvalidOperationException("运行身份与活动工作稿不一致。");
        if (book.Revisions.Head(chapterId).FormalId is not null) throw new InvalidOperationException("本章已定稿，请先明确回退。");
        if (book.Chapters.TakeWhile(c => c.Id != chapterId).Any(c => book.Revisions.Head(c.Id) is { WorkingId: null, FormalId: null }))
            throw new InvalidOperationException("前面的章节尚无有效稿件，不能跳章生成。");
        if (book.Chapters.SkipWhile(c => c.Id != chapterId).Skip(1).Any(c => book.Revisions.Head(c.Id).WorkingId is not null))
            throw new InvalidOperationException("后续已有工作稿，请先处理依赖关系。");
        foreach (var previous in book.Chapters.TakeWhile(c => c.Id != chapterId))
        {
            var head = book.Revisions.Head(previous.Id); var revision = book.Revisions.Get(head.WorkingId ?? head.FormalId)!;
            if (head.WorkingId is not null && revision.Check != RevisionCheck.Passed) throw new InvalidOperationException("前文工作稿尚未通过必要检查，不能自动续写。");
            if (revision.Check == RevisionCheck.Passed && !EditingRules.IsReviewCurrent(book, revision)) throw new InvalidOperationException("前文审校已过期，请先复核前文。");
            if (RevisionRules.Hash(previous.Text) != revision.TextHash) throw new InvalidOperationException("前文存在未提交编辑，请先处理后再续写。");
            if (head.WorkingId is not null)
                foreach (var fact in revision.Facts) StoryMemory.ValidateAcceptedFact(book, previous.Id, revision.RunId, revision.Text, fact);
        }
        var rules = RuleEvaluation.Check(book, chapterId, runId, "");
        if (!rules.Complete || rules.Conflicts.Length > 0) throw new InvalidOperationException("规则存在冲突或超出检查容量，未发送生成请求。");
    }
    public static void ValidateReview(ChapterReview review)
    {
        if (review is null || string.IsNullOrWhiteSpace(review.Summary) || review.Summary.Length > 10000 || review.Issues.IsDefault || review.Issues.Length > 100 || review.Facts.IsDefault || review.Facts.Length > 100 ||
            review.Issues.Any(i => i is null || !Enum.IsDefined(i.Severity) || !Enum.IsDefined(i.Category) || string.IsNullOrWhiteSpace(i.Message) || i.Message.Length > 2000 || i.Evidence is null || i.Evidence.Length > 2000) ||
            review.Facts.Any(f => f is null || f.Field is null || f.Evidence is null)) throw new InvalidDataException("章节审校报告结构无效。");
    }
    /// <summary>模型报告只能增加证据，不能覆盖本地硬规则结果。任何未完成检查、事实歧义或锁定剧情变化都阻止续写。</summary>
    public static ChapterWork Assess(BookProject book, StoryContext context, ChapterWork work, ChapterReview review)
    {
        ValidateReview(review); var issues = review.Issues.ToBuilder(); var facts = ImmutableArray.CreateBuilder<FactDelta>();
        void Block(ReviewCategory category, string message) => issues.Add(new(ReviewSeverity.Hard, category, message, ""));
        if (!review.RulesChecked || !review.FactsChecked || !review.PlotChecked || !review.StyleChecked) Block(ReviewCategory.Rule, "必要审校尚未全部完成。");
        if (!review.LockedPlanPreserved) Block(ReviewCategory.PlotMethod, "本次正文未保持章纲和锁定剧情。");
        foreach (var issue in review.Issues)
            if (issue.Evidence.Length > 0 && issue.Locate(work.Text) < 0) Block(issue.Category, "审校引用不在当前正文中，需要重新复核。");
        if (work.Characters < work.TargetCharacters * 0.8 || work.Characters > work.TargetCharacters * 1.2) Block(ReviewCategory.Style, $"本地字数 {work.Characters} 超出目标 {work.TargetCharacters} 的 ±20% 容差。");
        var local = RuleEvaluation.Check(book, work.ChapterId, work.RunId, work.Text);
        if (!local.Complete || local.Conflicts.Length > 0) Block(ReviewCategory.Rule, "本地规则检查未完成或规则冲突。");
        foreach (var finding in local.Findings) issues.Add(new(finding.Strength == WritingRuleStrength.Hard ? ReviewSeverity.Hard : ReviewSeverity.Advice, ReviewCategory.Rule, finding.Message, finding.Evidence));
        foreach (var fact in review.Facts)
        {
            try
            {
                facts.Add(StoryMemory.ValidateCandidate(book, context, new(book.Id, work.ChapterId, fact.EntityId, fact.Kind, fact.Field, fact.ExpectedValue, fact.NewValue,
                    fact.Evidence, context.Stamp, RevisionRules.Hash(work.Text))
                { Time = fact.Time, RelatedEntityId = fact.RelatedEntityId }, work.Text));
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { Block(ReviewCategory.Fact, error.Message); }
        }
        if (facts.Select(f => f.Key).Distinct().Count() != facts.Count) Block(ReviewCategory.Fact, "事实增量包含重复字段，需明确最终状态。");
        var accepted = !issues.Any(i => i.Severity != ReviewSeverity.Advice);
        var allIssues = issues.ToImmutable();
        return work with
        {
            Review = review,
            Issues = allIssues,
            Facts = facts.ToImmutable(),
            State = accepted ? ChapterWorkState.Ready : ChapterWorkState.NeedsAttention,
            Attempts = work.Attempts.Add(new(work.Attempts.Length + 1, work.Text, review, allIssues)),
            Message = accepted ? "必要检查已通过，可提交工作稿。" : "检查存在阻塞项，保留候选待处理。"
        };
    }
    public static string ApplyPatches(string text, ChapterPatches patches)
    {
        if (patches is null || patches.Patches.IsDefaultOrEmpty || patches.Patches.Length > 10) throw new InvalidDataException("局部修正需要 1–10 个明确片段。");
        var changed = 0; var result = text;
        foreach (var patch in patches.Patches)
        {
            if (patch is null || string.IsNullOrEmpty(patch.OldText) || patch.NewText is null) throw new InvalidDataException("修正片段不能为空。");
            var index = result.IndexOf(patch.OldText, StringComparison.Ordinal);
            if (index < 0 || result.IndexOf(patch.OldText, index + 1, StringComparison.Ordinal) >= 0) throw new InvalidDataException("修正原文缺失或不唯一，不能猜测替换位置。");
            changed = checked(changed + Math.Max(patch.OldText.Length, patch.NewText.Length));
            if (changed > text.Length * 0.3) throw new InvalidOperationException("修正超过正文 30%，请作者明确重写，未自动扩大修改范围。");
            result = result[..index] + patch.NewText + result[(index + patch.OldText.Length)..];
        }
        return result;
    }
    public static BookProject Commit(BookProject book, ChapterWork work)
    {
        work.Validate();
        var existing = book.Revisions.History.SingleOrDefault(r => r.OperationId == work.Id);
        if (existing is not null)
        {
            if (work.BookId != book.Id || existing.ChapterId != work.ChapterId || existing.RunId != work.RunId || existing.Text != work.Text || existing.Summary != work.Review?.Summary || !existing.Facts.SequenceEqual(work.Facts))
                throw new InvalidOperationException("同一章节操作不能提交不同内容。");
            return book;
        }
        if (work.BookId != book.Id || work.State != ChapterWorkState.Ready || work.Review is null || work.Issues.Any(i => i.Severity != ReviewSeverity.Advice)) throw new InvalidOperationException("候选尚未通过完整检查。");
        Preflight(book, work.ChapterId, work.RunId);
        if (work.SourceStamp != StoryMemory.Stamp(book, work.ChapterId, work.RunId, true)) throw new InvalidOperationException("候选生成后正文或规范已变化，不能覆盖当前作品。");
        var context = new StoryContext(book.Id, work.ChapterId, work.RunId, true, work.SourceStamp, [], [], 0);
        // 提交前重新执行本地证据门禁；不再次收费，也不复用修正前正文的事实元数据。
        var verified = Assess(book, context, work with { Attempts = [] }, work.Review);
        if (verified.State != ChapterWorkState.Ready || !verified.Facts.SequenceEqual(work.Facts)) throw new InvalidOperationException("提交前的事实或必要检查已失效。");
        var chapter = book.Chapters.Single(c => c.Id == work.ChapterId); var head = book.Revisions.Head(chapter.Id);
        if (RevisionRules.Hash(chapter.Text) != work.OriginalTextHash) throw new InvalidOperationException("作者编辑稿已变化。");
        var updated = book with { Chapters = book.Chapters.Replace(chapter, chapter with { Text = work.Text, Summary = work.Review.Summary }) };
        var submission = new DraftSubmission(book.Id, chapter.Id, head.WorkingId, head.FormalId, RevisionRules.Hash(work.Text), RevisionRules.ContextStamp(book, chapter.Id),
            work.Text, work.Review.Summary, work.Facts, RevisionCheck.Passed, work.RunId, work.Id)
        { ReviewPolicyStamp = StoryMemory.PolicyStamp(updated, chapter.Id, work.RunId) };
        return updated with { Revisions = RevisionRules.CommitWorking(updated, submission) };
    }
}
