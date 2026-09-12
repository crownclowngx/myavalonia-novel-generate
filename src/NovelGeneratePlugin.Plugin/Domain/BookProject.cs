using System.Collections.Immutable;
namespace NovelGeneratePlugin.Domain;

public sealed record Chapter(Guid Id, Guid VolumeId, string Title, string Outline, string Text, string Summary = "")
{
    public ChapterPlan Plan { get; init; } = ChapterPlan.Empty;
}
public sealed record Volume(Guid Id, string Title) { public string Goal { get; init; } = ""; }

/// <summary>本书的当前编辑快照；保存它不代表定稿或 AI 检查通过。</summary>
public sealed record BookProject(Guid Id, string Title, string Idea, ImmutableArray<Volume> Volumes, ImmutableArray<Chapter> Chapters)
{
    public WritingProfile Profile { get; init; } = WritingProfile.Empty;
    public TemplateAdoption? AdoptedTemplate { get; init; }
    public ConnectionBinding? Connection { get; init; }
    public WritingRuleSet Rules { get; init; } = WritingRuleSet.Empty;
    public RuleDraft RuleEditor { get; init; } = RuleDraft.Empty;
    public ImmutableArray<LocalRuleCheck> RuleChecks { get; init; } = [];
    public RevisionLedger Revisions { get; init; } = RevisionLedger.Empty;
    public StoryCatalog Story { get; init; } = StoryCatalog.Empty;
    public PlanningLedger Planning { get; init; } = PlanningLedger.Empty;
    public static BookProject Create(string title, string idea = "")
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("新建作品需要书名。", nameof(title));
        var volume = new Volume(Guid.NewGuid(), "第一卷");
        var project = new BookProject(Guid.NewGuid(), title.Trim(), idea, [volume],
            [new Chapter(Guid.NewGuid(), volume.Id, "第一章", "", "")]);
        project.Validate();
        return project;
    }
    public void Validate()
    {
        if (Id == Guid.Empty || Title is null || Title.Length > 200 || Idea is null)
            throw new InvalidDataException("作品身份或书名无效，书名最长 200 个字符。");
        if (Volumes.IsDefaultOrEmpty || Chapters.IsDefaultOrEmpty || Volumes.Length > 1000 || Chapters.Length > 10000)
            throw new InvalidDataException("项目需要有效的卷章，最多支持 1000 卷、10000 章。");
        var ids = new HashSet<Guid> { Id };
        foreach (var volume in Volumes)
            if (volume is null || volume.Id == Guid.Empty || !ids.Add(volume.Id) || volume.Title is null || volume.Title.Length > 200 || volume.Goal is null || volume.Goal.Length > 10000)
                throw new InvalidDataException("卷身份或标题无效。");
        foreach (var chapter in Chapters)
            if (chapter is null || chapter.Id == Guid.Empty || !ids.Add(chapter.Id) ||
                !Volumes.Any(v => v.Id == chapter.VolumeId) || chapter.Title is null || chapter.Title.Length > 200 || chapter.Text is null || chapter.Outline is null || chapter.Summary is null)
                throw new InvalidDataException("章节身份、归属或内容无效。");
        foreach (var chapter in Chapters) { if (chapter.Plan is null) throw new InvalidDataException("章纲不能为空。"); chapter.Plan.Validate(); }
        if (Planning is null) throw new InvalidDataException("规划记录不能为空。");
        Planning.Validate(this);
        if (Story is null) throw new InvalidDataException("故事实体目录不能为空。");
        Story.Validate();
        if (Revisions is null) throw new InvalidDataException("修订状态不能为空。");
        Revisions.Validate(this);
        if (Profile is null) throw new InvalidDataException("本书规范不能为空。");
        Profile.Validate();
        if (Rules is null || RuleEditor is null || RuleChecks.IsDefault) throw new InvalidDataException("规则、草案或检查记录无效。");
        Rules.Validate(this);
        if (RuleEditor.Original is null || RuleEditor.Interpretation is null || RuleEditor.Pattern is null || RuleEditor.ExceptionsText is null || RuleEditor.Source is null ||
            !Enum.IsDefined(RuleEditor.Kind) || !Enum.IsDefined(RuleEditor.Scope) || !Enum.IsDefined(RuleEditor.Strength)) throw new InvalidDataException("规则草案无效。");
        if (RuleChecks.Any(c => c is null || !Chapters.Any(ch => ch.Id == c.ChapterId) || c.TextHash is null || c.RulesStamp is null || c.Findings.IsDefault || c.Conflicts.IsDefault))
            throw new InvalidDataException("本地检查记录无效。");
        if (RuleChecks.Any(c => c.GuidanceCount < 0 || c.Findings.Any(f => f is null || f.RuleId == Guid.Empty || f.RuleVersion < 1 || !Enum.IsDefined(f.Strength) ||
            f.Start < -1 || f.Length < 0 || f.Start == -1 && f.Length != 0 || f.Message is null || f.Evidence is null) ||
            c.Conflicts.Any(f => f is null || f.FirstRuleId == Guid.Empty || f.SecondRuleId == Guid.Empty || f.Message is null)))
            throw new InvalidDataException("本地检查的命中证据无效。");
        if (Connection is { } binding && (binding.ConnectionId == Guid.Empty || binding.Version < 1 || binding.Name is null))
            throw new InvalidDataException("本书连接绑定无效。");
        if (AdoptedTemplate is { } adopted)
        {
            if (adopted.TemplateId == Guid.Empty || adopted.VersionId == Guid.Empty || adopted.VersionNumber < 1 || adopted.Name is null || adopted.SourceContent is null ||
                adopted.Dimensions == ProfileDimensions.None || (adopted.Dimensions & ~ProfileDimensions.All) != 0)
                throw new InvalidDataException("模板采用来源无效。");
            adopted.SourceContent.Validate();
        }
    }
    public BookProject CopyAsNew()
    {
        var volumes = Volumes.ToDictionary(v => v.Id, v => v with { Id = Guid.NewGuid() });
        var copy = this with
        {
            Revisions = RevisionLedger.Empty,
            Planning = PlanningLedger.Empty,
            Rules = Rules with { Items = Rules.Items.Select(r => r.Scope == WritingRuleScope.Volume ? r with { ScopeId = volumes[r.ScopeId!.Value].Id } : r).ToImmutableArray() },
            RuleChecks = [],
            Id = Guid.NewGuid(),
            Volumes = Volumes.Select(v => volumes[v.Id]).ToImmutableArray(),
            Chapters = Chapters.Select(c => c with { Id = Guid.NewGuid(), VolumeId = volumes[c.VolumeId].Id }).ToImmutableArray()
        };
        copy = PlanningRules.CopyIdentities(this, copy);
        return RevisionRules.CopyRevisionIdentities(this, copy);
    }
}
