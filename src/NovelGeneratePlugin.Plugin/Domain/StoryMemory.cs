using System.Collections.Immutable;
using System.Text.Json;
namespace NovelGeneratePlugin.Domain;

public enum StoryEntityKind { Person, Place, Item, Organization }
public enum StoryFactTime { Established, Planned, Uncertain }
public enum StoryFactKind { State, Relationship, Event, Possession, Knowledge, Foreshadow }
public sealed record StoryEntity(Guid Id, StoryEntityKind Kind, string Name, ImmutableArray<string> Aliases, string Description,
    ImmutableDictionary<string, string> LockedFields);
public sealed record StoryEntityDraft(Guid? Id, StoryEntityKind Kind, string Name, string Aliases, string Description, string LockedField, string LockedValue)
{
    public static StoryEntityDraft Empty { get; } = new(null, StoryEntityKind.Person, "", "", "", "", "");
    public static StoryEntityDraft From(StoryEntity entity) => new(entity.Id, entity.Kind, entity.Name, string.Join('\n', entity.Aliases), entity.Description,
        entity.LockedFields.OrderBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault().Key ?? "", entity.LockedFields.OrderBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault().Value ?? "");
}
/// <summary>实体身份只在书内解释。改名保留 ID，旧称成为别名；描述是作者设定，不作为已经发生的剧情证据。</summary>
public sealed record StoryCatalog(long Version, ImmutableArray<StoryEntity> Entities, StoryEntityDraft Editor)
{
    public static StoryCatalog Empty { get; } = new(0, [], StoryEntityDraft.Empty);
    public void Validate()
    {
        if (Version < 0 || Entities.IsDefault || Entities.Length > 1000 || Editor is null || !Enum.IsDefined(Editor.Kind) ||
            Editor.Name is null || Editor.Aliases is null || Editor.Description is null || Editor.LockedField is null || Editor.LockedValue is null)
            throw new InvalidDataException("实体目录或编辑草案无效。");
        var ids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in Entities)
        {
            if (entity is null || entity.Id == Guid.Empty || !ids.Add(entity.Id) || !Enum.IsDefined(entity.Kind) || string.IsNullOrWhiteSpace(entity.Name) ||
                entity.Name.Length > 100 || entity.Aliases.IsDefault || entity.Aliases.Length > 30 || entity.Description is null || entity.Description.Length > 10000 ||
                entity.LockedFields is null || entity.LockedFields.Count > 50) throw new InvalidDataException("实体名称、身份或容量无效。");
            foreach (var name in entity.Aliases.Prepend(entity.Name))
                if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || !names.Add(name)) throw new InvalidDataException("实体名称和别名必须在本书中唯一。");
            foreach (var field in entity.LockedFields)
                if (string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 100 || string.IsNullOrWhiteSpace(field.Value) || field.Value.Length > 2000)
                    throw new InvalidDataException("锁定状态字段和值无效。");
        }
        if (Editor.Id is Guid editing && !ids.Contains(editing)) throw new InvalidDataException("实体草案引用已不存在的实体。");
    }
    public StoryCatalog Commit()
    {
        var draft = Editor; var previous = draft.Id is Guid id ? Entities.Single(e => e.Id == id) : null;
        var aliases = draft.Aliases.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (previous is not null && previous.Name != draft.Name.Trim()) aliases.Add(previous.Name);
        var fields = previous?.LockedFields ?? ImmutableDictionary<string, string>.Empty;
        if (previous is not null && draft.LockedField.Trim() != StoryEntityDraft.From(previous).LockedField && fields.ContainsKey(draft.LockedField.Trim()))
            throw new InvalidOperationException("锁定字段名称已存在，不能覆盖另一条锁定状态。");
        if (previous is not null) fields = fields.Remove(StoryEntityDraft.From(previous).LockedField);
        if (!string.IsNullOrWhiteSpace(draft.LockedField) || !string.IsNullOrWhiteSpace(draft.LockedValue)) fields = fields.SetItem(draft.LockedField.Trim(), draft.LockedValue.Trim());
        var entity = new StoryEntity(previous?.Id ?? Guid.NewGuid(), draft.Kind, draft.Name.Trim(), aliases.Where(a => a != draft.Name.Trim()).Distinct().ToImmutableArray(), draft.Description, fields);
        var result = this with { Version = checked(Version + 1), Entities = previous is null ? Entities.Add(entity) : Entities.Replace(previous, entity), Editor = StoryEntityDraft.From(entity) };
        result.Validate(); return result;
    }
}

public sealed record StoryFactCandidate(Guid BookId, Guid ChapterId, Guid EntityId, StoryFactKind Kind, string Field, string? ExpectedValue,
    string? NewValue, string Evidence, string ContextStamp, string TextHash)
{
    public StoryFactTime Time { get; init; } = StoryFactTime.Uncertain;
    public Guid? RelatedEntityId { get; init; }
}
public sealed record StoryEvidence(Guid ChapterId, Guid RevisionId, Guid RunId, bool Working, string Title, string Text, string Summary,
    ImmutableArray<FactDelta> Facts);
public sealed record ContextPart(string Purpose, string Source, string Text, bool Required);
public sealed record StoryContext(Guid BookId, Guid ChapterId, Guid? RunId, bool UseWorking, string Stamp,
    ImmutableArray<ContextPart> Parts, ImmutableArray<string> Omitted, int Utf8Bytes)
{
    public string Render() => string.Join("\n\n", Parts.Select(p => $"【{p.Purpose}；来源 {p.Source}】\n{p.Text}"));
}

/// <summary>只从第 N 章之前的有效指针读取，索引可以随时从原文重建。候选、未来章纲、其他运行不会成为历史事实。</summary>
public static class StoryMemory
{
    public static ImmutableArray<StoryEvidence> Before(BookProject book, Guid chapterId, bool useWorking)
    {
        book.Validate(); if (!book.Chapters.Any(c => c.Id == chapterId)) throw new InvalidOperationException("章节不属于本书。");
        var result = ImmutableArray.CreateBuilder<StoryEvidence>();
        foreach (var chapter in book.Chapters.TakeWhile(c => c.Id != chapterId))
        {
            var head = book.Revisions.Head(chapter.Id); var working = useWorking && head.WorkingId is not null;
            var revision = book.Revisions.Get(working ? head.WorkingId : head.FormalId);
            if (revision is null) continue;
            if (revision.ContextStamp != RevisionRules.ContextStamp(book, chapter.Id, useWorking)) throw new InvalidOperationException("前文章节的来源已过期，请回退或重新接受相关工作稿。");
            if (working && revision.RunId != book.Revisions.ActiveRunId) throw new InvalidOperationException("工作稿不属于当前运行。");
            result.Add(new(chapter.Id, revision.Id, revision.RunId, working, revision.Title, revision.Text, revision.Summary, revision.Facts));
        }
        return result.ToImmutable();
    }
    public static ImmutableArray<StoryEvidence> Search(BookProject book, Guid chapterId, bool useWorking, string query, int maximum = 12)
    {
        if (query.Length > 1000 || maximum is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(query));
        var terms = query.Split([' ', '，', ',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in book.Story.Entities)
            if (entity.Aliases.Prepend(entity.Name).Any(a => query.Contains(a, StringComparison.Ordinal)))
                foreach (var alias in entity.Aliases.Prepend(entity.Name)) terms.Add(alias);
        if (terms.Count == 0) return [];
        if (terms.Count > 256) throw new InvalidOperationException("检索词与别名展开超过 256 项，请缩小关键词。");
        var history = Before(book, chapterId, useWorking);
        if (history.Sum(e => (long)e.Text.Length + e.Summary.Length) > 4000000)
            throw new InvalidOperationException("前文检索超过当前 400 万字符容量，需在长篇检索增强后处理；未返回不完整的命中结果。");
        // 中文按有界字面子串检索，因此两字人名不会被分词器停用词规则吞掉；排序稳定，返回源修订而非索引副本。
        return history.Select(e => (Evidence: e, Score: terms.Count(t => e.Text.Contains(t, StringComparison.Ordinal) || e.Summary.Contains(t, StringComparison.Ordinal))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenByDescending(x => book.Chapters.IndexOf(book.Chapters.Single(c => c.Id == x.Evidence.ChapterId)))
            .Take(maximum).Select(x => x.Evidence).ToImmutableArray();
    }
    public static string Stamp(BookProject book, Guid chapterId, Guid? runId, bool useWorking) => RevisionRules.Hash(JsonSerializer.Serialize(new
    {
        book.Id,
        Chapter = book.Chapters.Single(c => c.Id == chapterId),
        book.Planning.Mainline,
        Volume = book.Volumes.Single(v => v.Id == book.Chapters.Single(c => c.Id == chapterId).VolumeId),
        book.Title,
        book.Idea,
        book.Profile,
        Story = CanonicalCatalog(book.Story),
        Rules = RuleEvaluation.Stamp(book, chapterId, runId),
        Run = runId,
        Revision = RevisionRules.ContextStamp(book, chapterId, useWorking),
        Working = useWorking,
        book.Revisions.ActiveRunId
    }));
    // 字典枚举顺序可能随进程的字符串散列变化；版本指纹必须使用排序字段，重开后不能误报过期。
    private static object CanonicalCatalog(StoryCatalog story) => new
    {
        story.Version,
        Entities = story.Entities.Select(e => new
        {
            e.Id,
            e.Kind,
            e.Name,
            e.Aliases,
            e.Description,
            LockedFields = e.LockedFields.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
        }).ToArray()
    };
    public static string FactKey(Guid entityId, StoryFactKind kind, string field, Guid? relatedEntityId = null) =>
        $"{entityId:N}/{kind}/{field}" + (relatedEntityId is Guid related ? $"/{related:N}" : "");
    public static string PolicyStamp(BookProject book, Guid chapterId, Guid? runId)
    {
        var basis = RevisionRules.Hash(JsonSerializer.Serialize(new
        {
            book.Id,
            book.Title,
            book.Idea,
            book.Profile,
            ChapterId = chapterId,
            book.Chapters.Single(c => c.Id == chapterId).Outline,
            Story = CanonicalCatalog(book.Story),
            Rules = RuleEvaluation.Stamp(book, chapterId, runId),
            Run = runId
        }));
        var plan = book.Chapters.Single(c => c.Id == chapterId).Plan;
        // 无规划的旧版作品保留 v6 指纹语义，避免升级后无故使旧工作稿失效。
        return plan == ChapterPlan.Empty && book.Planning.Mainline.Length == 0 ? basis : CanonicalJson.Hash(new { Basis = basis, plan, book.Planning.Mainline });
    }
    public static FactDelta ValidateCandidate(BookProject book, StoryContext context, StoryFactCandidate candidate, string generatedText)
    {
        if (candidate.BookId != book.Id || context.BookId != book.Id || candidate.ChapterId != context.ChapterId ||
            context.Stamp != Stamp(book, context.ChapterId, context.RunId, context.UseWorking) || candidate.ContextStamp != context.Stamp ||
            candidate.TextHash != RevisionRules.Hash(generatedText)) throw new InvalidOperationException("事实候选的作品、正文或上下文已过期。");
        if (candidate.Time != StoryFactTime.Established) throw new InvalidOperationException("计划和时间不明确的事件不能成为已发生事实。");
        var entity = book.Story.Entities.SingleOrDefault(e => e.Id == candidate.EntityId) ?? throw new InvalidOperationException("事实引用未知实体，请先登记身份。");
        if (candidate.RelatedEntityId is Guid related && !book.Story.Entities.Any(e => e.Id == related) ||
            candidate.Kind is StoryFactKind.Relationship or StoryFactKind.Possession && candidate.RelatedEntityId is null)
            throw new InvalidOperationException("关系与归属必须引用已登记的关联实体，不能用名称充当身份。");
        if (!Enum.IsDefined(candidate.Kind) || string.IsNullOrWhiteSpace(candidate.Field) || candidate.Field.Length > 100 || candidate.Field.Contains('/') ||
            candidate.NewValue?.Length > 2000 || string.IsNullOrWhiteSpace(candidate.Evidence) || candidate.Evidence.Length > 2000 ||
            !generatedText.Contains(candidate.Evidence, StringComparison.Ordinal)) throw new InvalidOperationException("事实缺少有效字段或本次正文证据。");
        Before(book, context.ChapterId, context.UseWorking); // 在投影旧值前确认所有工作来源仍有效。
        var key = FactKey(entity.Id, candidate.Kind, candidate.Field, candidate.RelatedEntityId);
        var memory = RevisionRules.MemoryBefore(book, context.ChapterId, context.UseWorking); memory.TryGetValue(key, out var previous);
        if (previous != candidate.ExpectedValue) throw new InvalidOperationException("事实旧值不符，不能覆盖有效故事状态。");
        if (entity.LockedFields.TryGetValue(candidate.Field, out var locked) && candidate.NewValue != locked) throw new InvalidOperationException("事实候选触碰作者锁定状态。");
        return new FactDelta(key, previous, candidate.NewValue)
        {
            EntityId = entity.Id,
            Kind = candidate.Kind,
            Field = candidate.Field,
            Evidence = candidate.Evidence,
            RelatedEntityId = candidate.RelatedEntityId,
            PolicyStamp = PolicyStamp(book, context.ChapterId, context.RunId),
            ValidatedTextHash = candidate.TextHash,
            ValidatedRunId = context.RunId
        };
    }
    /// <summary>验证和提交之间可能发生作者编辑，因此工作稿提交与人工定稿都必须重新核对候选使用的规范版本。</summary>
    public static void ValidateAcceptedFact(BookProject book, Guid chapterId, Guid runId, string text, FactDelta fact)
    {
        if (fact.EntityId is not Guid entityId) return;
        if (fact.PolicyStamp != PolicyStamp(book, chapterId, runId) || fact.ValidatedRunId != runId || fact.ValidatedTextHash != RevisionRules.Hash(text))
            throw new InvalidOperationException("事实验证后正文、运行或规范已变化，请重新提取并检查候选。");
        var entity = book.Story.Entities.Single(e => e.Id == entityId);
        if (entity.LockedFields.TryGetValue(fact.Field!, out var locked) && fact.NewValue != locked)
            throw new InvalidOperationException("事实与作者锁定状态冲突，不能提交或定稿。");
    }
}
