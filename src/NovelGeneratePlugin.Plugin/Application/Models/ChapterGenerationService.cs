using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

public interface IChapterWorkStore
{
    void Save(ChapterWork work);
    ChapterWork? Get(Guid id);
    IReadOnlyList<ChapterWork> Recent(Guid bookId, Guid chapterId);
}

/// <summary>
/// 单章调度只拥有一次候选生命周期，不改作者作品。生成、检查、局部修正均经过同一个预算入口；
/// 每轮修正后重新审校四个维度和全部本地规则，不能靠“修正成功”推断“检查通过”。
/// </summary>
public sealed class ChapterGenerationService(ConnectionService connections, ModelRequestService requests, IChapterWorkStore store)
{
    public Task<IReadOnlyList<ChapterWork>> RecentAsync(Guid bookId, Guid chapterId) => Task.Run(() => store.Recent(bookId, chapterId));
    public Task<ChapterWork> GenerateAsync(BookProject book, Guid chapterId, Guid runId, int targetCharacters, int maximumRepairs,
        RequestBudget budget, IProgress<ChapterWork>? progress, CancellationToken cancellationToken)
        => ExecuteAsync(book, chapterId, runId, targetCharacters, maximumRepairs, budget, progress, cancellationToken, false, null);

    /// <summary>手写稿复核复用同一套四维审校、摘要和事实提取，不额外生成正文，也不自动修改作者文字。</summary>
    public Task<ChapterWork> ReviewTextAsync(BookProject book, Guid chapterId, Guid runId, int targetCharacters,
        RequestBudget budget, IProgress<ChapterWork>? progress, CancellationToken cancellationToken)
        => ExecuteAsync(book, chapterId, runId, targetCharacters, 0, budget, progress, cancellationToken, true, null);

    /// <summary>选区改写只让模型返回替换文字，本地拼接选区外正文；完整复核可以拒绝候选，但不能擅自扩大改写范围。</summary>
    public Task<ChapterWork> RewriteAsync(BookProject book, Guid chapterId, Guid runId, int targetCharacters, SelectionEdit edit,
        RequestBudget budget, IProgress<ChapterWork>? progress, CancellationToken cancellationToken)
        => ExecuteAsync(book, chapterId, runId, targetCharacters, 0, budget, progress, cancellationToken, false, edit);

    private async Task<ChapterWork> ExecuteAsync(BookProject book, Guid chapterId, Guid runId, int targetCharacters, int maximumRepairs,
        RequestBudget budget, IProgress<ChapterWork>? progress, CancellationToken cancellationToken, bool reviewOnly, SelectionEdit? edit)
    {
        ChapterGenerationRules.Preflight(book, chapterId, runId); cancellationToken.ThrowIfCancellationRequested();
        var context = new StoryContextBuilder().Build(book, chapterId, runId, true);
        var drafting = reviewOnly ? null : await connections.FreezeAsync(book, ModelTask.Drafting).ConfigureAwait(false);
        var checking = await connections.FreezeAsync(book, ModelTask.Checking).ConfigureAwait(false);
        var chapter = book.Chapters.Single(c => c.Id == chapterId);
        var work = new ChapterWork(Guid.NewGuid(), book.Id, chapterId, runId, context.Stamp, RevisionRules.Hash(chapter.Text), targetCharacters,
            maximumRepairs, ChapterWorkState.Drafting, "", null, [], [], [], "正在生成候选正文，不修改作者编辑稿。");
        work = work with { SourceDescription = reviewOnly ? "手写稿重新审校" : edit is null ? "整章生成" : edit.Append ? "正文末尾续写" : $"选区改写：{edit.Start + 1} 起，共 {edit.Length} 字符" };
        if (edit is not null)
        {
            EditingRules.ReplaceSelection(chapter.Text, edit.Start, edit.Length, "", edit.Append);
            if (string.IsNullOrWhiteSpace(edit.Instruction) || edit.Instruction.Length > 2000) throw new InvalidOperationException("请填写 1–2000 字符的改写要求。");
        }
        work.Validate();
        void Save()
        {
            work = work with { UpdateSequence = checked(work.UpdateSequence + 1) };
            store.Save(work);
            // 显示端异常不能污染已经保存的业务状态。
            try { progress?.Report(work); } catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        Save();
        var input = context.Render() + "\n本书写作方法原文：\n" + book.Profile.Methods + "\n本书文风要求：\n" + book.Profile.Style;
        try
        {
            if (reviewOnly) work = work with { Text = chapter.Text };
            else if (edit is not null)
            {
                var replacement = new SelectionReplacementContract();
                var response = await requests.GenerateAsync(new(Guid.NewGuid(), drafting!,
                    "按作者要求只返回 Replacement 替换文字，不复述选区外正文。续写仅追加新段落。遵守本书全部规范、事实与章纲。只返回契约 JSON。",
                    input + "\n当前完整正文：\n" + chapter.Text + "\n选定原文：\n" + chapter.Text.Substring(edit.Start, edit.Length) +
                    "\n操作：" + (edit.Append ? "末尾续写" : "选区改写") + "\n作者要求：\n" + edit.Instruction, true)
                { Contract = replacement, AllowJsonWrapperRepair = true }, budget, null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (response.Completion != ModelCompletion.Complete) throw new InvalidDataException("改写截断，未采用任何替换。");
                work = work with { Text = EditingRules.ReplaceSelection(chapter.Text, edit.Start, edit.Length, replacement.Parse(response.Text).Replacement, edit.Append) };
            }
            else
            {
                var response = await requests.GenerateAsync(new(Guid.NewGuid(), drafting!,
                    $"根据章纲写一章中文小说正文，目标 {targetCharacters} 个非空白字符，允许 ±20%。只输出完整正文，不输出分析或代码围栏。严格遵守章纲、锁定状态和来源规则。",
                    input, false), budget, new WorkTextProgress(text => { work = work with { Text = text }; Save(); }), cancellationToken).ConfigureAwait(false);
                work = work with { Text = response.Text }; cancellationToken.ThrowIfCancellationRequested();
                if (response.Completion != ModelCompletion.Complete) { work = work with { State = ChapterWorkState.NeedsAttention, Message = "正文被截断，候选保留，未开始审校或提交。" }; Save(); return work; }
            }
            for (var repair = 0; ; repair++)
            {
                work = work with { State = ChapterWorkState.Reviewing, Message = "正在检查规则、事实、剧情方法与文风。" }; Save();
                var contract = new ChapterReviewContract();
                var reviewResponse = await requests.GenerateAsync(new(Guid.NewGuid(), checking,
                    "审校当前小说正文。RulesChecked/FactsChecked/PlotChecked/StyleChecked 只在对应检查完成后设 true。核对所有规范、前文状态、章纲与方法事件、文风。" +
                    "LockedPlanPreserved 仅在保持章纲和锁定剧情时为 true。问题区分 Hard 硬失败、Uncertain 待核实事实、Advice 建议；Evidence 必须逐字引用正文，无可定位证据用空串。" +
                    "Facts 只抽取本章已经发生且有原文证据的状态、关系、事件、物品归属、知情和伏笔变化。只能用提供的实体 Guid；只有影响后续状态或身份一致性的未登记实体、事实歧义才作为 Uncertain 阻塞问题；普通背景用品无需逐一登记，正文已明确保留的悬念不是事实矛盾。" +
                    "Field 用简短字段名，不含 /；Evidence 必须是本章正文的连续原文，不可概括。ExpectedValue 来自前文事实，没有则 null。计划不算已发生。关系和归属必须有 RelatedEntityId，其他没有则 null。Summary 简要概括本章。只返回契约 JSON。",
                    input + "\n待审正文：\n" + work.Text, true)
                { Contract = contract, AllowJsonWrapperRepair = true }, budget, null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (reviewResponse.Completion != ModelCompletion.Complete) { work = work with { State = ChapterWorkState.NeedsAttention, Message = "审校报告截断，必要检查未完成。" }; Save(); return work; }
                work = ChapterGenerationRules.Assess(book, context, work, contract.Parse(reviewResponse.Text)); Save();
                if (work.State == ChapterWorkState.Ready || repair >= maximumRepairs) return work;
                work = work with { State = ChapterWorkState.Repairing, Message = $"第 {repair + 1} 次局部修正，之后重新执行全部检查。" }; Save();
                var patches = new ChapterPatchContract();
                var repaired = await requests.GenerateAsync(new(Guid.NewGuid(), drafting!,
                    "仅为列出的阻塞问题返回局部替换片段。OldText 必须是正文中唯一的逐字原文；NewText 是替换文字。不能更改章纲或锁定剧情，不能全篇改写，累计修改最多正文的 30%。只返回 JSON。",
                    input + "\n正文：\n" + work.Text + "\n问题：\n" + JsonSerializer.Serialize(work.Issues), true)
                { Contract = patches, AllowJsonWrapperRepair = true }, budget, null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (repaired.Completion != ModelCompletion.Complete) { work = work with { State = ChapterWorkState.NeedsAttention, Message = "修正片段截断，保留修正前正文。" }; Save(); return work; }
                work = work with { Text = ChapterGenerationRules.ApplyPatches(work.Text, patches.Parse(repaired.Text)), Review = null, Facts = [] }; Save();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            work = work with
            {
                State = cancellationToken.IsCancellationRequested ? ChapterWorkState.Cancelled : ChapterWorkState.Failed,
                Message = cancellationToken.IsCancellationRequested ? "已取消，已有候选保留；未提交作品。" : "任务停止，候选和已完成检查保留。" + error.Message
            };
            Save(); return work;
        }
    }
    private sealed class WorkTextProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}

public sealed class ChapterReviewContract : IModelOutputContract
{
    internal static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    public ChapterReview Parse(string text)
    {
        var result = JsonSerializer.Deserialize<ChapterReview>(text, Options) ?? throw new InvalidDataException("审校为空。"); ChapterGenerationRules.ValidateReview(result); return result;
    }
    public void Validate(JsonElement value) => Parse(value.GetRawText());
    public string JsonSchema => """
    {"type":"object","additionalProperties":false,"required":["RulesChecked","FactsChecked","PlotChecked","StyleChecked","LockedPlanPreserved","Summary","Issues","Facts"],"properties":{
    "RulesChecked":{"type":"boolean"},"FactsChecked":{"type":"boolean"},"PlotChecked":{"type":"boolean"},"StyleChecked":{"type":"boolean"},"LockedPlanPreserved":{"type":"boolean"},"Summary":{"type":"string"},
    "Issues":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["Severity","Category","Message","Evidence"],"properties":{"Severity":{"type":"string","enum":["Hard","Uncertain","Advice"]},"Category":{"type":"string","enum":["Rule","Fact","PlotMethod","Style"]},"Message":{"type":"string"},"Evidence":{"type":"string"}}}},
    "Facts":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["EntityId","Kind","Field","ExpectedValue","NewValue","Evidence","Time","RelatedEntityId"],"properties":{
    "EntityId":{"type":"string"},"Kind":{"type":"string","enum":["State","Relationship","Event","Possession","Knowledge","Foreshadow"]},"Field":{"type":"string"},"ExpectedValue":{"type":["string","null"]},"NewValue":{"type":["string","null"]},"Evidence":{"type":"string"},"Time":{"type":"string","enum":["Established","Planned","Uncertain"]},"RelatedEntityId":{"type":["string","null"]}}}}}}
    """;
}
public sealed class ChapterPatchContract : IModelOutputContract
{
    public ChapterPatches Parse(string text)
    {
        var result = JsonSerializer.Deserialize<ChapterPatches>(text, ChapterReviewContract.Options) ?? throw new InvalidDataException("修正为空。");
        if (result.Patches.IsDefaultOrEmpty || result.Patches.Length > 10 || result.Patches.Any(p => p is null || string.IsNullOrEmpty(p.OldText) || p.NewText is null)) throw new InvalidDataException("修正片段无效。");
        return result;
    }
    public void Validate(JsonElement value) => Parse(value.GetRawText());
    public string JsonSchema => """
    {"type":"object","additionalProperties":false,"required":["Patches"],"properties":{"Patches":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["OldText","NewText"],"properties":{"OldText":{"type":"string"},"NewText":{"type":"string"}}}}}}
    """;
}
