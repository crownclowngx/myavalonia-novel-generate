using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

/// <summary>
/// 上下文是可解释的不可变清单，不是一个无来源的大字符串。硬规则/锁定设定先占预算，装不下直接停止；
/// 其余部分完整加入或明确列入省略清单，不能从中间截断规则、事实或证据。
/// </summary>
public sealed class StoryContextBuilder
{
    public StoryContext Build(BookProject book, Guid chapterId, Guid? runId, bool useWorking, int maximumBytes = 100000, string query = "")
    {
        book.Validate(); if (maximumBytes is < 100 or > 500000) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (useWorking && book.Revisions.ActiveRunId is Guid active && runId != active) throw new InvalidOperationException("上下文不能读取另一运行的工作稿。");
        var history = StoryMemory.Before(book, chapterId, useWorking); var chapter = book.Chapters.Single(c => c.Id == chapterId);
        var parts = ImmutableArray.CreateBuilder<ContextPart>(); var omitted = ImmutableArray.CreateBuilder<string>(); var used = 0;
        void Add(string purpose, string source, string text, bool required)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var part = new ContextPart(purpose, source, text, required);
            var bytes = Encoding.UTF8.GetByteCount($"【{purpose}；来源 {source}】\n{text}") + (parts.Count == 0 ? 0 : 2);
            if (used + bytes > maximumBytes)
            {
                if (required) throw new InvalidOperationException("上下文预算装不下必须携带的内容：" + purpose + "。请提高预算或整理设定，未发送请求。");
                omitted.Add(purpose + " · " + source); return;
            }
            parts.Add(part); used += bytes;
        }
        Add("任务边界", book.Id.ToString(), "仅使用本书当前章之前已接受的故事事实；人物设定和章纲是约束/计划，不代表已经发生。复杂倒叙、知情推演与时间歧义必须提示复核。", true);
        Add("本书创意", "作者", book.Title + "\n" + book.Idea, true);
        Add("当前章规划", chapter.Id.ToString(), chapter.Title + "\n" + chapter.Outline, true);
        Add("世界与锁定设定", "本书规范快照", book.Profile.World, true);
        Add("长期规范原文", "本书规范快照", book.Profile.Rules, true);
        foreach (var rule in RuleEvaluation.Applicable(book, chapterId, runId).Where(r => r.Strength == WritingRuleStrength.Hard))
            Add("长期规则", $"{rule.Id}/v{rule.Version}/{rule.Scope}", JsonSerializer.Serialize(rule), rule.Strength == WritingRuleStrength.Hard);
        foreach (var entity in book.Story.Entities.Where(e => e.LockedFields.Count > 0))
            Add("锁定实体状态", entity.Id.ToString(), JsonSerializer.Serialize(entity), true);
        var memory = RevisionRules.MemoryBefore(book, chapterId, useWorking);
        var sources = history.SelectMany(e => e.Facts.Select(f => new { Fact = f, e.ChapterId, e.RevisionId, e.RunId, e.Working }))
            .GroupBy(f => f.Fact.Key).Select(g => g.Last()).Where(f => memory.ContainsKey(f.Fact.Key)).ToArray();
        foreach (var fact in sources)
            if (fact.Fact.EntityId is Guid owner && book.Story.Entities.Single(e => e.Id == owner).LockedFields.TryGetValue(fact.Fact.Field!, out var locked) && locked != fact.Fact.NewValue)
                throw new InvalidOperationException("锁定状态与已接受前文冲突，请先复核设定和故事事实。");
        Add("有效前文事实", "当前稿件指针与来源修订投影", JsonSerializer.Serialize(sources), true);
        foreach (var rule in RuleEvaluation.Applicable(book, chapterId, runId).Where(r => r.Strength == WritingRuleStrength.Advisory))
            Add("建议规则", $"{rule.Id}/v{rule.Version}", JsonSerializer.Serialize(rule), false);
        Add("文风", "本书规范快照", book.Profile.Style, false);
        Add("写作方法", "本书规范快照", book.Profile.Methods, false);
        foreach (var entity in book.Story.Entities.Where(e => e.LockedFields.Count == 0))
            Add("实体登记（不是剧情事实）", entity.Id.ToString(), JsonSerializer.Serialize(entity), false);

        foreach (var evidence in history.TakeLast(3).Reverse())
            Add("近期摘要", $"{evidence.ChapterId}/{evidence.RevisionId}/{(evidence.Working ? "工作稿" : "正式稿")}", evidence.Summary, false);
        var searchQuery = string.IsNullOrWhiteSpace(query) ? chapter.Outline[..Math.Min(1000, chapter.Outline.Length)] : query;
        var search = StoryMemory.Search(book, chapterId, useWorking, searchQuery);
        foreach (var evidence in search)
            Add("历史正文证据", $"{evidence.ChapterId}/{evidence.RevisionId}", evidence.Text, false);
        return new(book.Id, chapterId, runId, useWorking, StoryMemory.Stamp(book, chapterId, runId, useWorking), parts.ToImmutable(), omitted.ToImmutable(), used);
    }
}
