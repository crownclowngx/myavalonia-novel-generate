namespace NovelGeneratePlugin.Domain;

public sealed record BookEditResult(BookProject Project, Guid SelectedChapterId);

/// <summary>卷章操作是纯领域计算；ViewModel 只提交结果并选择新章节，不决定正文归属和插入顺序。</summary>
public static class BookEdits
{
    public static BookEditResult AddChapter(BookProject project, Guid? selectedChapterId)
    {
        project.Validate();
        var volumeId = selectedChapterId is null ? project.Volumes[0].Id : project.Chapters.Single(c => c.Id == selectedChapterId).VolumeId;
        var chapter = new Chapter(Guid.NewGuid(), volumeId, $"第 {project.Chapters.Length + 1} 章", "", "");
        var last = project.Chapters.LastOrDefault(c => c.VolumeId == volumeId);
        var index = last is null ? project.Chapters.Length : project.Chapters.IndexOf(last) + 1;
        var result = project with { Chapters = project.Chapters.Insert(index, chapter) };
        result.Validate(); return new BookEditResult(result, chapter.Id);
    }
    public static BookEditResult AddVolume(BookProject project)
    {
        project.Validate();
        var volume = new Volume(Guid.NewGuid(), $"第 {project.Volumes.Length + 1} 卷");
        var chapter = new Chapter(Guid.NewGuid(), volume.Id, $"第 {project.Chapters.Length + 1} 章", "", "");
        var result = project with { Volumes = project.Volumes.Add(volume), Chapters = project.Chapters.Add(chapter) };
        result.Validate(); return new BookEditResult(result, chapter.Id);
    }
}
