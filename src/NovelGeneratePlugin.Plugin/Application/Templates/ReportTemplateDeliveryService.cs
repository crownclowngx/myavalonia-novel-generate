using System.Text;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Templates;

/// <summary>候选交付是本地用例，与模型执行分责；失败时原候选仍在，重复交付核对来源而不改写目标草案。</summary>
public sealed class ReportTemplateDeliveryService(IReportTemplateConversionStore store, TemplateLibrary library)
{
    public async Task<ReportTemplateConversion> DeliverAsync(Guid id)
    {
        using var lease = store.Acquire(id); var run = store.Read(id);
        if (run.State == TemplateConversionState.DraftSaved) { await library.ReadAsync(run.TemplateId).ConfigureAwait(false); return run; }
        if (run.State != TemplateConversionState.CandidateSaved || run.Candidate is null) throw new InvalidOperationException("规范候选尚未完整保存，不能交付模板。");
        var profile = Profile(run.Candidate);
        var provenance = new TemplateProvenance(run.Id, run.Source.BookId, run.Source.RunId, run.Source.SourceId, run.Source.ReportVersion,
            run.Source.TextHash, run.Source.Stamp, CanonicalJson.Hash(profile), run.UpdatedAt);
        var draft = new TemplateDraft(run.Name, ["小说分析"], profile, $"来自小说分析：{run.Source.Name}\n报告版本 {run.Source.ReportVersion}\n转换 {run.Id}") { Provenance = provenance };
        await library.CreateGeneratedDraftAsync(run.TemplateId, draft).ConfigureAwait(false);
        var saved = run with
        {
            Revision = run.Revision + 1,
            State = TemplateConversionState.DraftSaved,
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = "新模板草案已保存；打开编辑后保存新版本，即可供作品采用。"
        };
        try { store.Save(saved, run.Revision); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 模板已经落盘。若这里中断，下次仍使用相同目标和生成时间，库用例只读回已有草案。
            try { store.WriteRecovery(saved); }
            catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
            { throw new TemplateConversionSaveException("模板草案已保存，但任务登记与恢复文件失败；可在模板库找到它，继续时会核对同一目标。", error); }
            throw new TemplateConversionSaveException("模板草案已保存，任务登记待恢复；继续会核对已有模板，不会再创建一份。", error);
        }
        return saved;
    }
    public static WritingProfile Profile(TemplateConversionCandidate candidate)
    {
        string Field(ProfileDimensions dimension)
        {
            var section = candidate.Sections.SingleOrDefault(s => s.Dimension == dimension);
            if (section is null) return "";
            // 长期规则会进入作品的持续约束，推断和建议仅在预览保留，不能自动变成硬规则原文。
            return string.Join("\n", section.Rules.Where(r => dimension != ProfileDimensions.Rules || r.Basis == TemplateRuleBasis.Supported)
                .Select(r => "• " + r.Text + (r.Condition == "" ? "" : "（适用：" + r.Condition + "）")));
        }
        return new(Field(ProfileDimensions.World), Field(ProfileDimensions.Style), Field(ProfileDimensions.Methods), Field(ProfileDimensions.Rules));
    }
    public static string Preview(ReportTemplateConversion run)
    {
        if (run.Candidate is null) return "规范候选尚未完成。成功子步骤已保存，失败时可继续。";
        var text = new StringBuilder();
        foreach (var section in run.Candidate.Sections)
        {
            text.AppendLine(Title(section.Dimension));
            foreach (var rule in section.Rules)
                text.AppendLine($"• {rule.Text}" + (rule.Condition == "" ? "" : $"（适用：{rule.Condition}）"))
                    .AppendLine($"  {Basis(rule.Basis)}；依据结论 {string.Join("、", rule.Sources.Select(id => "S" + id))}" +
                    (section.Dimension == ProfileDimensions.Rules && rule.Basis != TemplateRuleBasis.Supported ? "；未自动写入长期规则，可在草案中自行补充。" : ""));
            foreach (var question in section.Questions) text.AppendLine("待处理：" + question);
            text.AppendLine();
        }
        return text.ToString();
    }
    public static string Title(ProfileDimensions dimension) => dimension switch
    { ProfileDimensions.World => "世界观与基础设定", ProfileDimensions.Style => "文风规范", ProfileDimensions.Methods => "写作方法", _ => "长期规则" };
    public static string Basis(TemplateRuleBasis value) => value switch
    { TemplateRuleBasis.Supported => "来源明确支持", TemplateRuleBasis.Inferred => "归纳推断", _ => "创作建议或待确认" };
}
