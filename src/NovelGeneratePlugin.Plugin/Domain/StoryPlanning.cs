using System.Collections.Immutable;
using System.Text.Json;
namespace NovelGeneratePlugin.Domain;

public enum PlanningMode { Automatic, Collaborative }
public sealed record MethodApplication(string SourceQuote, string SetupEvent, string PressureEvent, string PayoffEvent);
/// <summary>章纲字段可作为编辑草案自动保存；ReadyStamp 只表示通过当前结构/来源门禁，不代表作者已经定稿。</summary>
public sealed record ChapterPlan(string Goal, string Conflict, string Viewpoint, string TimePlace, string Events,
    string StateChanges, string Foreshadow, string Bridge, ImmutableArray<MethodApplication> Methods, bool Locked, string ReadyStamp)
{
    public static ChapterPlan Empty { get; } = new("", "", "", "", "", "", "", "", [], false, "");
    public void Validate(bool ready = false)
    {
        var fields = new[] { Goal, Conflict, Viewpoint, TimePlace, Events, StateChanges, Foreshadow, Bridge, ReadyStamp };
        if (fields.Any(s => s is null || s.Length > 10000) || Methods.IsDefault || Methods.Length > 20 ||
            Methods.Any(m => m is null || new[] { m.SourceQuote, m.SetupEvent, m.PressureEvent, m.PayoffEvent }.Any(s => s is null || s.Length > 2000 || ready && string.IsNullOrWhiteSpace(s))))
            throw new InvalidDataException("章纲字段或写作方法应用无效。");
        if (ready && new[] { Goal, Conflict, Viewpoint, TimePlace, Events, StateChanges, Bridge }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("可生成章纲需要目标、冲突、视角、时地、事件、状态变化和承接。");
    }
    public string Render() => $"目标：{Goal}\n冲突：{Conflict}\n视角：{Viewpoint}\n时地：{TimePlace}\n事件：{Events}\n状态变化（计划）：{StateChanges}\n伏笔：{Foreshadow}\n承接：{Bridge}" +
        string.Concat(Methods.Select(m => $"\n方法来源：{m.SourceQuote}\n铺设：{m.SetupEvent}\n阻碍：{m.PressureEvent}\n兑现：{m.PayoffEvent}"));
}
public sealed record PlannedChapterSnapshot(Guid ChapterId, Guid VolumeId, string Title, string Outline, ChapterPlan Plan);
public sealed record PlannedVolumeSnapshot(Guid VolumeId, string Title, string Goal);
public sealed record PlanningRevision(Guid Id, Guid OperationId, int Number, string Source, DateTimeOffset CreatedAt, string Mainline,
    ImmutableArray<PlannedVolumeSnapshot> Volumes, ImmutableArray<PlannedChapterSnapshot> Chapters);
public sealed record PlanningLedger(string Mainline, ImmutableArray<PlanningRevision> History)
{
    public PlanningCandidate? Pending { get; init; }
    public static PlanningLedger Empty { get; } = new("", []);
    public void Validate(BookProject book)
    {
        if (Mainline is null || Mainline.Length > 20000 || History.IsDefault || History.Length > 2000) throw new InvalidDataException("主线或规划历史无效。");
        if (Pending is not null)
        {
            if (Pending.BookId != book.Id || Pending.OperationId == Guid.Empty || !book.Chapters.Any(c => c.Id == Pending.StartChapterId) || Pending.SourceStamp is null || Pending.Source is null)
                throw new InvalidDataException("待采用规划的身份无效。");
            PlanningRules.ValidateProposal(Pending.Proposal);
        }
        var ids = new HashSet<Guid>(); var operations = new HashSet<Guid>();
        for (var index = 0; index < History.Length; index++)
        {
            var entry = History[index];
            if (entry is null || entry.Id == Guid.Empty || entry.OperationId == Guid.Empty || !ids.Add(entry.Id) || !operations.Add(entry.OperationId) ||
                entry.Number != index + 1 || entry.Source is null || entry.Mainline is null || entry.Volumes.IsDefault || entry.Chapters.IsDefault)
                throw new InvalidDataException("规划历史身份或内容无效。");
            foreach (var chapter in entry.Chapters)
            {
                if (chapter is null || !book.Chapters.Any(c => c.Id == chapter.ChapterId) || !book.Volumes.Any(v => v.Id == chapter.VolumeId) ||
                    chapter.Title is null || chapter.Outline is null || chapter.Plan is null) throw new InvalidDataException("规划历史章节归属无效。");
                chapter.Plan.Validate();
            }
            if (entry.Volumes.Any(v => v is null || !book.Volumes.Any(b => b.Id == v.VolumeId) || v.Title is null || v.Goal is null)) throw new InvalidDataException("规划历史分卷无效。");
        }
    }
    public void EnsureAppendOnlyFrom(PlanningLedger previous)
    {
        if (History.Length < previous.History.Length) throw new InvalidOperationException("不能删除规划修订历史。");
        // 历史是只含稳定标量与数组的快照，序列化深比较避免 ImmutableArray 的底层引用差异。
        for (var i = 0; i < previous.History.Length; i++)
            if (JsonSerializer.Serialize(previous.History[i]) != JsonSerializer.Serialize(History[i])) throw new InvalidOperationException("已有规划修订不能原地覆盖。");
    }
}

public sealed record ProposedVolume(string Title, string Goal);
public sealed record ProposedChapter(int Volume, string Title, string Goal, string Conflict, string Viewpoint, string TimePlace,
    string Events, string StateChanges, string Foreshadow, string Bridge, ImmutableArray<MethodApplication> Methods)
{
    public ChapterPlan ToPlan() => new(Goal, Conflict, Viewpoint, TimePlace, Events, StateChanges, Foreshadow, Bridge, Methods, false, "");
}
public sealed record ProposedEntity(string Name, StoryEntityKind Kind, ImmutableArray<string> Aliases, string Description);
public sealed record PlanningProposal(string Mainline, ImmutableArray<ProposedVolume> Volumes, ImmutableArray<ProposedChapter> Chapters, ImmutableArray<ProposedEntity> Entities);
public sealed record PlanningCandidate(Guid BookId, Guid StartChapterId, string SourceStamp, Guid OperationId, string Source, PlanningProposal Proposal);

/// <summary>规划只修改计划字段，按既有章序复用身份，绝不删除或覆盖正文。自动与协作模式共用同一采用门禁。</summary>
public static class PlanningRules
{
    public static string SourceStamp(BookProject book) => CanonicalJson.Hash(book with { Planning = book.Planning with { Pending = null } });
    public static void ValidateProposal(PlanningProposal proposal)
    {
        if (proposal is null || string.IsNullOrWhiteSpace(proposal.Mainline) || proposal.Mainline.Length > 20000 || proposal.Volumes.IsDefaultOrEmpty || proposal.Volumes.Length > 3 ||
            proposal.Chapters.IsDefault || proposal.Chapters.Length is < 3 or > 5 || proposal.Entities.IsDefault || proposal.Entities.Length > 30)
            throw new InvalidDataException("规划需要全书主线、1–3 卷与 3–5 章，实体建议最多 30 项。");
        if (proposal.Volumes.Any(v => v is null || string.IsNullOrWhiteSpace(v.Title) || v.Title.Length > 200 || string.IsNullOrWhiteSpace(v.Goal) || v.Goal.Length > 10000))
            throw new InvalidDataException("分卷标题或目标无效。");
        var previousVolume = 0; var volumeIds = new HashSet<int>();
        foreach (var chapter in proposal.Chapters)
        {
            if (chapter is null || chapter.Volume < 1 || chapter.Volume > proposal.Volumes.Length || chapter.Volume < previousVolume || string.IsNullOrWhiteSpace(chapter.Title) || chapter.Title.Length > 200)
                throw new InvalidDataException("章纲卷序或标题无效。");
            chapter.ToPlan().Validate(true); previousVolume = chapter.Volume; volumeIds.Add(chapter.Volume);
        }
        if (volumeIds.Count != proposal.Volumes.Length) throw new InvalidDataException("规划不能生成没有近期章节的空卷。");
        var catalog = new StoryCatalog(0, proposal.Entities.Select(e => e is null ? throw new InvalidDataException("实体建议为空。") :
            new StoryEntity(Guid.NewGuid(), e.Kind, e.Name, e.Aliases, e.Description, ImmutableDictionary<string, string>.Empty)).ToImmutableArray(), StoryEntityDraft.Empty);
        catalog.Validate();
    }
    public static string ReadyStamp(BookProject book, Guid chapterId)
    {
        var chapter = book.Chapters.Single(c => c.Id == chapterId); var volume = book.Volumes.Single(v => v.Id == chapter.VolumeId);
        return RevisionRules.Hash(JsonSerializer.Serialize(new
        {
            book.Title,
            book.Idea,
            book.Profile,
            StoryVersion = book.Story.Version,
            RuleVersion = book.Rules.Version,
            book.Planning.Mainline,
            VolumeTitle = volume.Title,
            volume.Goal,
            ChapterTitle = chapter.Title,
            chapter.Outline,
            Plan = chapter.Plan with { ReadyStamp = "", Locked = false }
        }));
    }
    public static void RequireReady(BookProject book, Guid chapterId)
    {
        var chapter = book.Chapters.Single(c => c.Id == chapterId); chapter.Plan.Validate(ready: true);
        if (string.IsNullOrWhiteSpace(book.Planning.Mainline) || chapter.Plan.ReadyStamp != ReadyStamp(book, chapterId)) throw new InvalidOperationException("章纲尚未保存为有效规划，或本书规范已变化，请重新保存/生成章纲。");
        ValidateMethods(book, chapter.Plan);
    }
    public static void ValidateMethods(BookProject book, ChapterPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(book.Profile.Methods) && plan.Methods.IsEmpty) throw new InvalidDataException("本书已有写作方法，需要落实到具体剧情事件。");
        if (plan.Methods.Any(m => !book.Profile.Methods.Contains(m.SourceQuote, StringComparison.Ordinal))) throw new InvalidDataException("方法来源必须引用本书方法原文，不能伪造来源。");
    }
    public static void CheckTargets(BookProject book, Guid startChapterId, int count)
    {
        book.Validate(); if (count is < 3 or > 5) throw new InvalidDataException("近期规划需要 3–5 章。");
        if (!book.Chapters.Any(c => c.Id == startChapterId) || string.IsNullOrWhiteSpace(book.Idea)) throw new InvalidOperationException("请先填写本书创意并选择规划起始章。");
        foreach (var chapter in book.Chapters.SkipWhile(c => c.Id != startChapterId).Take(count))
            if (chapter.Plan.Locked || book.Revisions.Head(chapter.Id) is { WorkingId: not null } or { FormalId: not null })
                throw new InvalidOperationException("目标范围含锁定计划或已接受修订，请从未接受的章节开始规划。");
        var check = RuleEvaluation.Check(book, startChapterId, book.Revisions.ActiveRunId, "");
        if (check.Conflicts.Length > 0 || !check.Complete) throw new InvalidOperationException("本书规则存在确定性冲突或超出检查容量，暂不能生成规划。");
    }
    public static BookProject Apply(BookProject book, PlanningCandidate candidate)
    {
        if (candidate.BookId != book.Id || candidate.SourceStamp != SourceStamp(book)) throw new InvalidOperationException("规划期间作品已变化，候选不能覆盖当前内容。");
        var proposal = candidate.Proposal; ValidateProposal(proposal); CheckTargets(book, candidate.StartChapterId, proposal.Chapters.Length);
        var start = book.Chapters.IndexOf(book.Chapters.Single(c => c.Id == candidate.StartChapterId));
        var volumes = book.Volumes; var targetVolumes = new List<Guid>();
        // 提案中的卷序是本次局部规划序号，从当前章所属卷开始；旧卷和旧章不删除。
        var firstVolume = volumes.IndexOf(volumes.Single(v => v.Id == book.Chapters[start].VolumeId));
        for (var i = 0; i < proposal.Volumes.Length; i++)
        {
            var proposed = proposal.Volumes[i]; var position = firstVolume + i;
            if (position < volumes.Length)
            {
                var previous = volumes[position];
                // 已有正文卷的标题属于作者；规划只补充分卷目标。
                volumes = volumes.SetItem(position, previous with { Goal = proposed.Goal }); targetVolumes.Add(previous.Id);
            }
            else { var volume = new Volume(Guid.NewGuid(), proposed.Title) { Goal = proposed.Goal }; volumes = volumes.Add(volume); targetVolumes.Add(volume.Id); }
        }
        var chapters = book.Chapters; var changedIds = new List<Guid>();
        for (var i = 0; i < proposal.Chapters.Length; i++)
        {
            var proposed = proposal.Chapters[i]; var plan = proposed.ToPlan(); plan.Validate(true); ValidateMethods(book, plan);
            var volumeId = targetVolumes[proposed.Volume - 1]; var position = start + i;
            if (position < chapters.Length)
            {
                var previous = chapters[position];
                if (previous.VolumeId != volumeId) throw new InvalidOperationException("局部规划不能移动已有章节到另一卷，请调整规划范围或卷结构。");
                var updated = previous with { Title = string.IsNullOrWhiteSpace(previous.Text) ? proposed.Title : previous.Title, Outline = plan.Render(), Plan = plan };
                chapters = chapters.SetItem(position, updated); changedIds.Add(updated.Id);
            }
            else { var chapter = new Chapter(Guid.NewGuid(), volumeId, proposed.Title, plan.Render(), "") { Plan = plan }; chapters = chapters.Add(chapter); changedIds.Add(chapter.Id); }
        }
        var entities = book.Story.Entities;
        foreach (var proposed in proposal.Entities)
        {
            if (entities.Any(e => e.Name == proposed.Name || e.Aliases.Contains(proposed.Name))) continue;
            entities = entities.Add(new(Guid.NewGuid(), proposed.Kind, proposed.Name, proposed.Aliases, proposed.Description, ImmutableDictionary<string, string>.Empty));
        }
        var updatedBook = book with
        {
            Volumes = volumes,
            Chapters = chapters,
            Planning = book.Planning with { Mainline = proposal.Mainline, Pending = null },
            Story = book.Story with { Entities = entities, Version = entities == book.Story.Entities ? book.Story.Version : checked(book.Story.Version + 1) }
        };
        updatedBook.Validate();
        foreach (var id in changedIds) updatedBook = MarkReady(updatedBook, id);
        return Record(updatedBook, candidate.OperationId, candidate.Source);
    }
    public static BookProject MarkReady(BookProject book, Guid chapterId)
    {
        var chapter = book.Chapters.Single(c => c.Id == chapterId); chapter.Plan.Validate(true); ValidateMethods(book, chapter.Plan);
        if (string.IsNullOrWhiteSpace(book.Planning.Mainline)) throw new InvalidDataException("请填写全书主线。");
        return book with { Chapters = book.Chapters.Replace(chapter, chapter with { Plan = chapter.Plan with { ReadyStamp = ReadyStamp(book, chapterId) } }) };
    }
    public static BookProject Record(BookProject book, Guid operationId, string source)
    {
        var ledger = book.Planning; if (ledger.History.Any(r => r.OperationId == operationId)) throw new InvalidOperationException("规划操作已记录，不能重复采用。");
        var revision = new PlanningRevision(Guid.NewGuid(), operationId, ledger.History.Length + 1, source, DateTimeOffset.UtcNow, ledger.Mainline,
            book.Volumes.Select(v => new PlannedVolumeSnapshot(v.Id, v.Title, v.Goal)).ToImmutableArray(),
            book.Chapters.Select(c => new PlannedChapterSnapshot(c.Id, c.VolumeId, c.Title, c.Outline, c.Plan)).ToImmutableArray());
        var result = book with { Planning = ledger with { History = ledger.History.Add(revision) } }; result.Validate(); return result;
    }
    public static BookProject CopyIdentities(BookProject source, BookProject copy)
    {
        var chapters = source.Chapters.Select((c, i) => (c.Id, NewId: copy.Chapters[i].Id)).ToDictionary(p => p.Id, p => p.NewId);
        var volumes = source.Volumes.Select((v, i) => (v.Id, NewId: copy.Volumes[i].Id)).ToDictionary(p => p.Id, p => p.NewId);
        copy = copy with
        {
            Planning = source.Planning with
            {
                Pending = null,
                History = source.Planning.History.Select(r => r with
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    Volumes = r.Volumes.Select(v => v with { VolumeId = volumes[v.VolumeId] }).ToImmutableArray(),
                    Chapters = r.Chapters.Select(c => c with { ChapterId = chapters[c.ChapterId], VolumeId = volumes[c.VolumeId] }).ToImmutableArray()
                }).ToImmutableArray()
            }
        };
        return copy;
    }
}
