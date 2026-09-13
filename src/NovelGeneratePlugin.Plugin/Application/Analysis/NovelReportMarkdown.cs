using System.Text;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>Markdown 为本地格式化产物，模型只提供受契约约束的文字与事实编号。来源标识、覆盖和质量状态均由本地计算。</summary>
public static class NovelReportMarkdown
{
    public static string Format(NovelAnalysisReport report)
    {
        var text = new StringBuilder(); var used = new HashSet<int>();
        text.AppendLine("# " + Escape(report.Name) + " · 小说分析报告").AppendLine();
        text.AppendLine(report.IsComplete ? "状态：提供文件的全文分析候选已齐全。" : "状态：部分报告；专题或综合结论尚未齐全，不代表分析完成。").AppendLine();
        text.AppendLine($"提取覆盖：{report.CoveredCharacters}/{report.SourceCharacters} 个 UTF-16 字符。范围：完整提供文件，原著完整性未经确认。").AppendLine();
        text.AppendLine(Escape(report.QualityStatus) + "。模型：" + Escape(report.Model) + "。").AppendLine();
        if (!report.ExecutionSummary.IsEmpty)
        {
            text.AppendLine("各阶段实际执行配置（修订可保留旧参数的成功节点）：").AppendLine();
            foreach (var item in report.ExecutionSummary) text.AppendLine("- " + Escape(item));
            text.AppendLine();
        }
        text.AppendLine($"来源文件：{Escape(report.FileName)}  ").AppendLine($"原始字节 SHA-256：`{report.ByteHash}`  ")
            .AppendLine($"文本 SHA-256：`{report.TextHash}`  ").AppendLine($"报告版本：`{report.Version}`").AppendLine();
        if (report.Synthesis is not null) Section("综合结论", report.Synthesis);
        else text.AppendLine("## 综合结论").AppendLine().AppendLine("尚未完成。").AppendLine();
        foreach (var dimension in Enum.GetValues<AnalysisDimension>())
        {
            var part = report.Parts.SingleOrDefault(p => p.Dimension == dimension);
            if (part is null) text.AppendLine("## " + NovelReportRequests.Title(dimension)).AppendLine().AppendLine("尚未完成。").AppendLine();
            else Section(NovelReportRequests.Title(dimension), part.Draft);
        }
        text.AppendLine("## 身份索引").AppendLine().AppendLine("同名/别名不明确的身份保留独立条目。提及次数是当前提取索引计数，不是对原文姓名出现频率的精确统计。").AppendLine();
        text.AppendLine("| 身份 | 判断 | 提及数 | 归并依据与限制 |").AppendLine("| --- | --- | --- | --- |");
        foreach (var identity in report.Integrated.Identities)
            text.AppendLine($"| {Escape(identity.Name)} | {Kind(identity.Certainty)} | {identity.Mentions.Length} | {Escape(identity.Reason).Replace("\n", " ", StringComparison.Ordinal)}{(identity.PossibleSameMentions.IsEmpty ? "" : "；存在未确认的同一身份候选")} |");
        text.AppendLine().AppendLine("## 覆盖与未解决问题").AppendLine();
        text.AppendLine($"已保存 {report.Integrated.Index.Chunks.Length} 个提取单元、{report.Integrated.Index.Mentions.Length} 条提及和 {report.Integrated.Index.Findings.Length} 条原始观察；{report.Integrated.UnintegratedFacts.Length} 条观察尚未进入跨章整合，原始索引仍保留。").AppendLine();
        text.AppendLine("文风原句按固定等距规则从前、中、后期选取；专题叙述与综合结论是模型分析候选，未进行人工准确率与召回率验收。").AppendLine();
        foreach (var question in report.Integrated.OpenQuestions) text.AppendLine("- " + Escape(question));
        foreach (var (chunk, number) in report.Integrated.Index.Chunks.Select((chunk, i) => (chunk, i + 1)))
            foreach (var gap in chunk.Gaps) text.AppendLine($"- 单元 {number} · {NovelReportRequests.Title(gap.Dimension)}：{Escape(gap.Reason)}");
        text.AppendLine().AppendLine("## 依据与原文定位").AppendLine().AppendLine("编号关联保存的原始观察。下方每段为原文节选，区间为半开 UTF-16 坐标，全文来源哈希见报告开头。").AppendLine();
        foreach (var id in used.Order())
        {
            var finding = report.Integrated.Index.Findings.Single(f => f.Number == id);
            text.AppendLine($"<a id=\"fact-{id}\"></a>").AppendLine().AppendLine($"### F{id} · {Escape(finding.Value.Subject)}").AppendLine();
            text.AppendLine(Escape(finding.Value.Statement)).AppendLine();
            text.AppendLine($"原始判断：{Kind(finding.Value.Kind)}；叙述来源：{Narration(finding.Value.Narration)}；提取单元：{finding.Chunk}。").AppendLine();
            foreach (var evidence in finding.Value.Evidence.DistinctBy(e => e.Range))
            {
                var quote = Snippet(evidence.Quote, 240); var section = report.Sections.Single(s => s.Range.Start <= evidence.Range.Start && s.Range.End > evidence.Range.Start);
                text.AppendLine($"{Escape(section.Title)}，原文节选区间 [{evidence.Range.Start}, {evidence.Range.Start + quote.Length})：").AppendLine();
                foreach (var line in quote.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) text.AppendLine("> " + Escape(line));
                text.AppendLine();
            }
        }
        return text.ToString();

        void Section(string title, NovelReportDraft draft)
        {
            text.AppendLine("## " + title).AppendLine();
            foreach (var claim in draft.Claims)
            {
                text.AppendLine("### " + Escape(claim.Heading)).AppendLine().AppendLine(Escape(claim.Text)).AppendLine();
                foreach (var id in claim.Facts) used.Add(id);
                text.AppendLine($"判断：{Kind(claim.Certainty)}。依据：" + string.Join("、", claim.Facts.Select(id => $"[F{id}](#fact-{id})")) + "。").AppendLine();
            }
            if (!draft.OpenQuestions.IsEmpty)
            {
                text.AppendLine("待确认：").AppendLine();
                foreach (var question in draft.OpenQuestions) text.AppendLine("- " + Escape(question)); text.AppendLine();
            }
        }
    }
    public static string Kind(AnalysisStatementKind kind) => kind switch { AnalysisStatementKind.Explicit => "明确", AnalysisStatementKind.Inferred => "推断", AnalysisStatementKind.Uncertain => "不确定", _ => "未知" };
    public static string Narration(NarrativeSource source) => source switch
    {
        NarrativeSource.Narration => "叙述",
        NarrativeSource.CharacterClaim => "角色说法",
        NarrativeSource.Rumor => "传闻",
        NarrativeSource.Dream => "梦境",
        NarrativeSource.Recollection => "回忆",
        NarrativeSource.Plan => "计划",
        _ => "未知"
    };
    private static string Snippet(string text, int maximum)
    { var length = Math.Min(text.Length, maximum); if (length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--; return text[..length]; }
    private static string Escape(string text)
    {
        var value = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        foreach (var symbol in new[] { "\\", "`", "*", "_", "[", "]", "#", "|" }) value = value.Replace(symbol, "\\" + symbol, StringComparison.Ordinal);
        return value;
    }
}
