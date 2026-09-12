using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
namespace NovelGeneratePlugin.Domain;

public enum RevisionCheck { NotChecked, Passed, Failed }
public sealed record FactDelta(string Key, string? ExpectedValue, string? NewValue);
public sealed record ChapterRevision(Guid Id, Guid ChapterId, Guid? ParentId, Guid RunId, Guid OperationId,
    string Title, string Text, string TextHash, string Summary, ImmutableArray<FactDelta> Facts,
    RevisionCheck Check, string ContextStamp, DateTimeOffset CreatedAt);
public sealed record ChapterHead(Guid ChapterId, Guid? WorkingId, Guid? FormalId);
public sealed record DraftSubmission(Guid ProjectId, Guid ChapterId, Guid? ExpectedWorkingId, Guid? ExpectedFormalId,
    string ExpectedTextHash, string ExpectedContextStamp, string Text, string Summary,
    ImmutableArray<FactDelta> Facts, RevisionCheck Check, Guid RunId, Guid OperationId);

/// <summary>
/// 修订内容只追加，工作/正式状态由指针表达。状态转换不修改历史正文，也不把编辑保存误作人工定稿。
/// 记忆由当前有效指针投影，放弃或回退时自然恢复到相同来源的事实，不维护另一份易失配的可变记忆表。
/// </summary>
public sealed record RevisionLedger(ImmutableArray<ChapterRevision> History, ImmutableArray<ChapterHead> Heads, Guid? ActiveRunId)
{
    public static RevisionLedger Empty { get; } = new([], [], null);
    public ChapterHead Head(Guid chapterId) => Heads.FirstOrDefault(h => h.ChapterId == chapterId) ?? new ChapterHead(chapterId, null, null);
    public ChapterRevision? Get(Guid? id) => id is null ? null : History.Single(r => r.Id == id);
    internal RevisionLedger WithHead(ChapterHead head)
    {
        var index = Heads.IndexOf(Heads.FirstOrDefault(h => h.ChapterId == head.ChapterId)!);
        return this with { Heads = index < 0 ? Heads.Add(head) : Heads.SetItem(index, head) };
    }
    public void Validate(BookProject project)
    {
        if (History.IsDefault || Heads.IsDefault || History.Any(r => r is null) || Heads.Any(h => h is null))
            throw new InvalidDataException("修订集合无效。");
        var chapterIds = project.Chapters.Select(c => c.Id).ToHashSet();
        var seen = new Dictionary<Guid, ChapterRevision>(); var operations = new HashSet<Guid>();
        foreach (var revision in History)
        {
            if (revision.Id == Guid.Empty || revision.RunId == Guid.Empty || revision.OperationId == Guid.Empty ||
                !chapterIds.Contains(revision.ChapterId) || !operations.Add(revision.OperationId) || !seen.TryAdd(revision.Id, revision) ||
                revision.Title is null || string.IsNullOrWhiteSpace(revision.Text) || revision.TextHash != RevisionRules.Hash(revision.Text) ||
                revision.Summary is null || revision.ContextStamp is null || revision.Facts.IsDefault || !Enum.IsDefined(revision.Check))
                throw new InvalidDataException("修订身份、正文摘要或检查状态无效。");
            if (revision.ParentId is Guid parent && (!seen.TryGetValue(parent, out var previous) || previous.Id == revision.Id || previous.ChapterId != revision.ChapterId))
                throw new InvalidDataException("修订父节点必须是同章已有修订。");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in revision.Facts)
                if (fact is null || string.IsNullOrWhiteSpace(fact.Key) || !keys.Add(fact.Key)) throw new InvalidDataException("事实键无效或重复。");
        }
        var headed = new HashSet<Guid>();
        foreach (var head in Heads)
        {
            if (!chapterIds.Contains(head.ChapterId) || !headed.Add(head.ChapterId)) throw new InvalidDataException("章节修订指针无效。");
            foreach (var id in new[] { head.WorkingId, head.FormalId })
                if (id is Guid value && (!seen.TryGetValue(value, out var revision) || revision.ChapterId != head.ChapterId))
                    throw new InvalidDataException("正文指针与章节归属不一致。");
            if (head.WorkingId is Guid working && seen[working].RunId != ActiveRunId)
                throw new InvalidDataException("只能存在一个活动运行的工作稿。");
        }
        if (ActiveRunId is not null && !Heads.Any(h => h.WorkingId is not null)) throw new InvalidDataException("空工作稿不能保留活动运行身份。");
        var encounteredGap = false;
        foreach (var chapter in project.Chapters)
        {
            if (Head(chapter.Id).FormalId is null) encounteredGap = true;
            else
            {
                if (encounteredGap) throw new InvalidDataException("正式稿必须从第一章连续定稿。");
                var projected = project with { Revisions = this };
                var revision = Get(Head(chapter.Id).FormalId)!;
                if (revision.ContextStamp != RevisionRules.ContextStamp(projected, chapter.Id, false))
                    throw new InvalidDataException("正式稿的前文上下文已变化，请先回退相关章节，不能直接重排。");
                var before = RevisionRules.MemoryBefore(projected, chapter.Id, false);
                foreach (var fact in revision.Facts)
                {
                    before.TryGetValue(fact.Key, out var old);
                    if (old != fact.ExpectedValue) throw new InvalidDataException("正式事实与前文旧值不一致：" + fact.Key);
                }
            }
        }
    }
    /// <summary>存储入口也执行历史前缀校验，避免未来用例绕过领域规则覆盖已存在的正文或记忆。</summary>
    public void EnsureAppendOnlyFrom(RevisionLedger previous)
    {
        if (History.Length < previous.History.Length) throw new InvalidOperationException("不能删除修订历史。");
        for (var i = 0; i < previous.History.Length; i++)
        {
            var a = previous.History[i]; var b = History[i];
            if (a with { Facts = b.Facts } != b || !a.Facts.SequenceEqual(b.Facts))
                throw new InvalidOperationException("已有修订不可变，请创建新修订。");
        }
    }
}

public static class RevisionRules
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string ContextStamp(BookProject project, Guid chapterId, bool useWorking = true)
    {
        var source = new StringBuilder(project.Id.ToString("N")).Append('|');
        foreach (var chapter in Before(project, chapterId))
        {
            var head = project.Revisions.Head(chapter.Id);
            var id = useWorking ? head.WorkingId ?? head.FormalId : head.FormalId;
            source.Append(chapter.Id).Append(':').Append(id).Append(';');
        }
        return Hash(source.ToString());
    }
    public static ImmutableDictionary<string, string> MemoryBefore(BookProject project, Guid chapterId, bool useWorking)
    {
        var memory = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var chapter in Before(project, chapterId))
        {
            var head = project.Revisions.Head(chapter.Id);
            var revision = project.Revisions.Get(useWorking ? head.WorkingId ?? head.FormalId : head.FormalId);
            if (revision is null) continue;
            foreach (var fact in revision.Facts)
            {
                memory.TryGetValue(fact.Key, out var previous);
                if (previous != fact.ExpectedValue) throw new InvalidDataException("故事记忆来源不一致：" + fact.Key);
                if (fact.NewValue is null) memory.Remove(fact.Key); else memory[fact.Key] = fact.NewValue;
            }
        }
        return memory.ToImmutable();
    }
    private static IEnumerable<Chapter> Before(BookProject project, Guid chapterId)
    {
        if (!project.Chapters.Any(c => c.Id == chapterId)) throw new InvalidOperationException("章节不属于当前作品。");
        return project.Chapters.TakeWhile(c => c.Id != chapterId);
    }
    public static RevisionLedger CommitWorking(BookProject project, DraftSubmission submission)
    {
        project.Validate(); var ledger = project.Revisions;
        if (project.Id != submission.ProjectId) throw new InvalidOperationException("修订提交不属于当前作品。");
        var chapter = project.Chapters.Single(c => c.Id == submission.ChapterId);
        var existing = ledger.History.FirstOrDefault(r => r.OperationId == submission.OperationId);
        if (existing is not null)
        {
            if (existing.ChapterId != submission.ChapterId || existing.Text != submission.Text || existing.Summary != submission.Summary ||
                existing.RunId != submission.RunId || existing.Check != submission.Check || !existing.Facts.SequenceEqual(submission.Facts) ||
                existing.ContextStamp != submission.ExpectedContextStamp || existing.TextHash != submission.ExpectedTextHash)
                throw new InvalidOperationException("同一操作身份不能提交不同内容。");
            return ledger;
        }
        var head = ledger.Head(chapter.Id);
        if (head.WorkingId != submission.ExpectedWorkingId || head.FormalId != submission.ExpectedFormalId ||
            Hash(chapter.Text) != submission.ExpectedTextHash || Hash(submission.Text) != submission.ExpectedTextHash)
            throw new InvalidOperationException("正文或修订已经变化，本次提交已过期。");
        if (submission.ExpectedContextStamp != ContextStamp(project, chapter.Id)) throw new InvalidOperationException("前文故事状态已经变化，请重新检查本章。");
        if (ledger.ActiveRunId is Guid active && active != submission.RunId) throw new InvalidOperationException("请先完成或放弃现有运行的工作稿。");
        if (head.FormalId is not null) throw new InvalidOperationException("本章已经定稿，请先回退本章及后续正式稿。");
        if (project.Chapters.SkipWhile(c => c.Id != chapter.Id).Skip(1).Any(c => ledger.Head(c.Id).WorkingId is not null))
            throw new InvalidOperationException("后续章已有工作稿，请先放弃本轮工作稿后再重写前文。");
        var memory = MemoryBefore(project, chapter.Id, true);
        if (submission.Facts.IsDefault) throw new InvalidOperationException("事实增量集合无效。");
        foreach (var fact in submission.Facts)
        {
            if (fact is null || string.IsNullOrWhiteSpace(fact.Key)) throw new InvalidDataException("事实键不能为空。");
            memory.TryGetValue(fact.Key, out var old);
            if (old != fact.ExpectedValue) throw new InvalidOperationException("事实旧值与有效前文不一致：" + fact.Key);
        }
        var revision = new ChapterRevision(Guid.NewGuid(), chapter.Id, head.WorkingId ?? head.FormalId, submission.RunId, submission.OperationId,
            chapter.Title, submission.Text, Hash(submission.Text), submission.Summary, submission.Facts, submission.Check, submission.ExpectedContextStamp, DateTimeOffset.UtcNow);
        var result = (ledger with { History = ledger.History.Add(revision), ActiveRunId = submission.RunId }).WithHead(head with { WorkingId = revision.Id });
        result.Validate(project); return result;
    }
    public static RevisionLedger FinalizeChapter(BookProject project, Guid chapterId, Guid expectedWorkingId, bool authorConfirmed)
    {
        project.Validate(); var ledger = project.Revisions; var head = ledger.Head(chapterId);
        if (head.WorkingId != expectedWorkingId) throw new InvalidOperationException("工作稿已变化，不能定稿旧版本。");
        var revision = ledger.Get(head.WorkingId)!;
        if (!authorConfirmed) throw new InvalidOperationException("定稿需要作者主动确认。");
        if (Hash(project.Chapters.Single(c => c.Id == chapterId).Text) != revision.TextHash)
            throw new InvalidOperationException("编辑稿已变化，请重新提交工作稿后再定稿。");
        if (revision.Check == RevisionCheck.Failed) throw new InvalidOperationException("检查失败的修订不能定稿，请先修正并重新提交。");
        if (Before(project, chapterId).Any(c => ledger.Head(c.Id).FormalId is null)) throw new InvalidOperationException("请按顺序定稿前面的章节。");
        if (revision.ContextStamp != ContextStamp(project, chapterId)) throw new InvalidOperationException("工作稿引用的前文已过期。");
        var result = ledger.WithHead(head with { WorkingId = null, FormalId = revision.Id });
        if (!result.Heads.Any(h => h.WorkingId is not null)) result = result with { ActiveRunId = null };
        result.Validate(project); return result;
    }
    /// <summary>恢复为独立作品时重分配所有身份，保留正文历史和稿件状态；有效活动上下文随新身份重建。</summary>
    public static BookProject CopyRevisionIdentities(BookProject source, BookProject copy)
    {
        var chapters = source.Chapters.Select((c, i) => (c.Id, NewId: copy.Chapters[i].Id)).ToDictionary(x => x.Id, x => x.NewId);
        var revisions = source.Revisions.History.ToDictionary(r => r.Id, _ => Guid.NewGuid());
        var runs = source.Revisions.History.Select(r => r.RunId).Distinct().ToDictionary(id => id, _ => Guid.NewGuid());
        var history = source.Revisions.History.Select(r => r with
        {
            Id = revisions[r.Id],
            ChapterId = chapters[r.ChapterId],
            ParentId = r.ParentId is Guid parent ? revisions[parent] : null,
            RunId = runs[r.RunId],
            OperationId = Guid.NewGuid()
        }).ToImmutableArray();
        var heads = source.Revisions.Heads.Select(h => new ChapterHead(chapters[h.ChapterId],
            h.WorkingId is Guid work ? revisions[work] : null, h.FormalId is Guid formal ? revisions[formal] : null)).ToImmutableArray();
        copy = copy with { Revisions = new RevisionLedger(history, heads, source.Revisions.ActiveRunId is Guid active ? runs[active] : null) };
        history = history.Select((r, i) =>
        {
            var old = source.Revisions.History[i]; var head = source.Revisions.Head(old.ChapterId);
            return (head.WorkingId == old.Id || head.FormalId == old.Id) && old.ContextStamp == ContextStamp(source, old.ChapterId)
                ? r with { ContextStamp = ContextStamp(copy, r.ChapterId) } : r;
        }).ToImmutableArray();
        copy = copy with { Revisions = copy.Revisions with { History = history } };
        copy.Validate(); return copy;
    }
    public static RevisionLedger DiscardWorking(BookProject project)
    {
        var result = project.Revisions with { Heads = project.Revisions.Heads.Select(h => h with { WorkingId = null }).ToImmutableArray(), ActiveRunId = null };
        result.Validate(project); return result;
    }
    public static RevisionLedger RollbackFrom(BookProject project, Guid chapterId)
    {
        var preceding = Before(project, chapterId).Select(c => c.Id).ToHashSet();
        // 前文回退会使后续记忆失效，因此一并撤销后续正式指针和本轮工作稿，历史正文仍保留。
        var result = project.Revisions with { Heads = project.Revisions.Heads.Select(h => h with { WorkingId = null, FormalId = preceding.Contains(h.ChapterId) ? h.FormalId : null }).ToImmutableArray(), ActiveRunId = null };
        result.Validate(project); return result;
    }
}
