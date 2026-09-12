using System.Collections.Immutable;
namespace NovelGeneratePlugin.Domain;

public sealed record ChapterImpact(Guid ChapterId, string Title, string Reason);
public sealed record TextDifference(int Start, string Before, string After)
{
    public string Describe() => Before == After ? "正文相同。" : $"从第 {Start + 1} 个字符起，原文 {Before.Length} 字符 → 新文 {After.Length} 字符。\n原文：\n{Before}\n新文：\n{After}";
}
/// <summary>
/// 改稿规则只处理不可变快照。差异是最小公共前后缀之间的完整区间，不猜测多个相似段落的对应关系。
/// 影响标记保守传播到后续章节，原文、正式指针和旧摘要均不自动改写。
/// </summary>
public static class EditingRules
{
    public static bool IsReviewCurrent(BookProject book, ChapterRevision revision) => revision.Check == RevisionCheck.Passed &&
        revision.ReviewPolicyStamp == StoryMemory.PolicyStamp(book, revision.ChapterId, revision.RunId) &&
        revision.ContextStamp == RevisionRules.ContextStamp(book, revision.ChapterId);

    public static ImmutableArray<ChapterImpact> Impacts(BookProject book)
    {
        var result = ImmutableArray.CreateBuilder<ChapterImpact>(); var upstream = false;
        foreach (var chapter in book.Chapters)
        {
            var head = book.Revisions.Head(chapter.Id); var revision = book.Revisions.Get(head.WorkingId ?? head.FormalId);
            var reason = revision is null ? "" : revision.TextHash != RevisionRules.Hash(chapter.Text) ? "编辑稿已变化，旧摘要和检查不适用于新正文。" :
                revision.Check == RevisionCheck.Passed && !IsReviewCurrent(book, revision) ? "规范或前文已变化，已通过的审校需要复核。" : "";
            if (reason.Length > 0 || upstream) result.Add(new(chapter.Id, chapter.Title, reason.Length > 0 ? reason : "前文存在待复核修改，后续内容保留，使用前需核对。"));
            upstream |= reason.Length > 0;
        }
        return result.ToImmutable();
    }
    public static TextDifference Difference(string before, string after)
    {
        var start = 0; while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        // 不把 UTF-16 代理对拆成不可显示的半个字符。
        if (start > 0 && start < before.Length && char.IsLowSurrogate(before[start])) start--;
        var end = 0; while (end < before.Length - start && end < after.Length - start && before[^(end + 1)] == after[^(end + 1)]) end++;
        if (end > 0 && char.IsLowSurrogate(before[before.Length - end])) end--;
        return new(start, before.Substring(start, before.Length - start - end), after.Substring(start, after.Length - start - end));
    }
    public static string ReplaceSelection(string text, int start, int length, string replacement, bool append)
    {
        if (start < 0 || length < 0 || start > text.Length - length || replacement is null || replacement.Length > 20000 ||
            (append ? start != text.Length || length != 0 : length == 0)) throw new InvalidOperationException("请选择有效正文片段；续写只能追加到正文末尾。");
        if (start < text.Length && char.IsLowSurrogate(text[start]) || start + length < text.Length && char.IsLowSurrogate(text[start + length]))
            throw new InvalidOperationException("选区不能拆开一个完整字符。");
        return text[..start] + replacement + text[(start + length)..];
    }
    /// <summary>先在内存依次验证整个范围，再由会话一次提交账本；任一章失败，不留下半个批量定稿。</summary>
    public static RevisionLedger FinalizeRange(BookProject book, int first, int last, bool authorConfirmed)
    {
        if (first < 1 || last > book.Chapters.Length || first > last) throw new InvalidOperationException("定稿范围无效。");
        var updated = book;
        foreach (var chapter in book.Chapters.Skip(first - 1).Take(last - first + 1))
        {
            var id = updated.Revisions.Head(chapter.Id).WorkingId ?? throw new InvalidOperationException(chapter.Title + " 缺少工作稿，整段未定稿。");
            updated = updated with { Revisions = RevisionRules.FinalizeChapter(updated, chapter.Id, id, authorConfirmed) };
        }
        return updated.Revisions;
    }
}
