using System.Collections.Immutable;
namespace NovelGeneratePlugin.Domain;

public sealed record Chapter(Guid Id, Guid VolumeId, string Title, string Outline, string Text, string Summary = "");
public sealed record Volume(Guid Id, string Title);

/// <summary>本书的当前编辑快照；保存它不代表定稿或 AI 检查通过。</summary>
public sealed record BookProject(Guid Id, string Title, string Idea, ImmutableArray<Volume> Volumes, ImmutableArray<Chapter> Chapters)
{
    public RevisionLedger Revisions { get; init; } = RevisionLedger.Empty;
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
            if (volume is null || volume.Id == Guid.Empty || !ids.Add(volume.Id) || volume.Title is null || volume.Title.Length > 200)
                throw new InvalidDataException("卷身份或标题无效。");
        foreach (var chapter in Chapters)
            if (chapter is null || chapter.Id == Guid.Empty || !ids.Add(chapter.Id) ||
                !Volumes.Any(v => v.Id == chapter.VolumeId) || chapter.Title is null || chapter.Title.Length > 200 || chapter.Text is null || chapter.Outline is null || chapter.Summary is null)
                throw new InvalidDataException("章节身份、归属或内容无效。");
        if (Revisions is null) throw new InvalidDataException("修订状态不能为空。");
        Revisions.Validate(this);
    }
    public BookProject CopyAsNew()
    {
        var volumes = Volumes.ToDictionary(v => v.Id, v => new Volume(Guid.NewGuid(), v.Title));
        var copy = this with
        {
            Revisions = RevisionLedger.Empty,
            Id = Guid.NewGuid(),
            Volumes = Volumes.Select(v => volumes[v.Id]).ToImmutableArray(),
            Chapters = Chapters.Select(c => c with { Id = Guid.NewGuid(), VolumeId = volumes[c.VolumeId].Id }).ToImmutableArray()
        };
        return RevisionRules.CopyRevisionIdentities(this, copy);
    }
}
