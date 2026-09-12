using System.Collections.Immutable;
using System.Text;
namespace NovelGeneratePlugin.Domain;

public enum ManuscriptVersion { Editing, WorkingView, Formal }
public enum ManuscriptFormat { Text, Markdown }
public sealed record ExportSelection(ManuscriptVersion Version, ManuscriptFormat Format, int FirstChapter, int LastChapter);
public sealed record ExportChapter(Guid ChapterId, Guid? RevisionId, string Title, string Text, LocalRuleCheck Check);
public sealed record PreparedManuscript(BookProject Source, ExportSelection Selection, string Content, ImmutableArray<ExportChapter> Chapters, int WordCount)
{
    public bool HasWarnings => Chapters.Any(c => !c.Check.Complete || c.Check.HasHardFailure || c.Check.Findings.Length > 0 || c.Check.GuidanceCount > 0);
}
/// <summary>导出只读明确选择的版本。工作稿视图优先工作指针、再用正式指针；缺少稿件时拒绝，绝不悄悄换成编辑稿。</summary>
public static class ManuscriptExport
{
    public static PreparedManuscript Prepare(BookProject book, ExportSelection selection)
    {
        book.Validate();
        if (!Enum.IsDefined(selection.Version) || !Enum.IsDefined(selection.Format) || selection.FirstChapter < 1 || selection.LastChapter > book.Chapters.Length || selection.FirstChapter > selection.LastChapter)
            throw new InvalidOperationException("请选择有效稿件版本、格式和章节范围。");
        var chapters = ImmutableArray.CreateBuilder<ExportChapter>();
        var text = new StringBuilder(); var markdown = selection.Format == ManuscriptFormat.Markdown;
        text.AppendLine(markdown ? "# " + Heading(book.Title) : book.Title).AppendLine();
        Guid? previousVolume = null; var count = 0;
        foreach (var chapter in book.Chapters.Skip(selection.FirstChapter - 1).Take(selection.LastChapter - selection.FirstChapter + 1))
        {
            var head = book.Revisions.Head(chapter.Id);
            var revision = selection.Version switch
            {
                ManuscriptVersion.Editing => null,
                ManuscriptVersion.WorkingView => book.Revisions.Get(head.WorkingId ?? head.FormalId) ?? throw new InvalidOperationException($"{chapter.Title} 尚无工作稿或正式稿，请先提交或选择编辑稿。"),
                ManuscriptVersion.Formal => book.Revisions.Get(head.FormalId) ?? throw new InvalidOperationException($"{chapter.Title} 尚未定稿，未生成不完整的正式稿导出。"),
                _ => throw new InvalidOperationException()
            };
            var body = revision?.Text ?? chapter.Text; var title = revision?.Title ?? chapter.Title;
            if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException($"{title} 正文为空，请缩小范围或完成正文。");
            var check = RuleEvaluation.Check(book, chapter.Id, revision?.RunId ?? book.Revisions.ActiveRunId, body);
            chapters.Add(new ExportChapter(chapter.Id, revision?.Id, title, body, check));
            if (previousVolume != chapter.VolumeId)
            {
                var volume = book.Volumes.Single(v => v.Id == chapter.VolumeId); previousVolume = volume.Id;
                text.AppendLine(markdown ? "## " + Heading(volume.Title) : volume.Title).AppendLine();
            }
            text.AppendLine(markdown ? "### " + Heading(title) : title).AppendLine();
            // 正文逐字保留，Markdown 是作者文本导出，不能为排版悄悄删改内容。
            text.AppendLine(body).AppendLine(); count += body.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r));
        }
        return new PreparedManuscript(book, selection, text.ToString(), chapters.ToImmutable(), count);
    }
    private static string Heading(string text) => text.Replace("\r", " ").Replace("\n", " ").Replace("\\", "\\\\").Replace("#", "\\#").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]");
}
