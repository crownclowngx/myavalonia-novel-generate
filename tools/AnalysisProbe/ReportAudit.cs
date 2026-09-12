using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// 开发阶段只读验收：无模型、连接或凭据依赖。自动检查结构与坐标，人工表格保留空白，绝不把模型自评当成准确率。
/// 每次生成新的评阅目录，不覆盖用户已经填写的评阅记录。
/// </summary>
internal static class ReportAudit
{
    public static async Task<string> RunAsync(WorkspacePaths paths, Guid runId)
    {
        var sources = new ReferenceSourceStore(paths); var runs = new AnalysisRunStore(paths); var reports = new NovelAnalysisReportService(sources, runs);
        var run = runs.Read(runId); var source = sources.Read(run.BookId); var readMilliseconds = new List<double>(); NovelAnalysisReport? report = null;
        for (var iteration = 0; iteration < 3; iteration++) { var timer = Stopwatch.StartNew(); report = reports.Read(runId); readMilliseconds.Add(timer.Elapsed.TotalMilliseconds); }
        if (report is not { IsComplete: true }) throw new InvalidOperationException("待验报告尚未齐全。");
        var claims = report.Parts.SelectMany(p => p.Draft.Claims).Concat(report.Synthesis!.Claims).ToArray();
        var facts = claims.SelectMany(c => c.Facts).Distinct().Order().ToArray();
        var evidence = report.Integrated.Index.Findings.Where(f => facts.Contains(f.Number)).SelectMany(f => f.Value.Evidence).Distinct().ToArray();
        var locateMilliseconds = new List<double>();
        foreach (var item in evidence)
        {
            var timer = Stopwatch.StartNew(); var location = reports.Locate(runId, item); locateMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            if (location.SourceHash != report.TextHash || location.Excerpt.Substring(location.SelectionStart, location.Length) != item.Quote) throw new InvalidDataException("真实引用无法精确定位。");
        }
        var usage = new ModelRequestStore(paths).List(run.Budget.Id);
        var summary = new
        {
            RunId = runId, ReportVersion = report.Version, report.SourceCharacters, report.CoveredCharacters, report.IsComplete,
            SourceByteHash = report.ByteHash, SourceTextHash = report.TextHash, Nodes = run.Nodes.Length,
            Stages = run.Nodes.GroupBy(n => n.Kind).Select(g => new { Kind = g.Key.ToString(), Count = g.Count() }),
            Claims = claims.Length, CitedFacts = facts.Length, LocatedEvidence = evidence.Length, ExactEvidenceMatches = evidence.Length,
            Requests = usage.Count, KnownTokens = usage.Where(e => e.Usage.InputTokens is not null && e.Usage.OutputTokens is not null).Sum(e => e.ChargedTokens),
            UnknownRequests = usage.Count(e => e.Usage.InputTokens is null || e.Usage.OutputTokens is null), ConservativeTokens = usage.Sum(e => e.ChargedTokens),
            ReportReadMilliseconds = readMilliseconds, MaximumLocateMilliseconds = locateMilliseconds.Max(),
            AverageLocateMilliseconds = locateMilliseconds.Average(), Machine = Environment.MachineName, Processors = Environment.ProcessorCount, Runtime = Environment.Version.ToString(),
            Scope = "complete supplied partial export; semantic quality and million-character real novel not verified"
        };
        var folder = Path.Combine(paths.Root, "G0036-audit-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder, "audit.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        var review = new StringBuilder("# 人工评阅包（尚未填写）\n\n")
            .AppendLine($"来源：{source.Source.FileName}；{report.SourceCharacters} UTF-16 字符。来源 SHA-256：`{report.ByteHash}`。报告版本：`{report.Version}`。")
            .AppendLine("\n本包自动整理评阅材料，不生成标准答案或准确率。文件为部分导出，未知原著结局保持未知。请先独立阅读原文标注事实，再逐条核对报告；所有分母、错误和修订需保留。")
            .AppendLine("\n## 主要人物、关键事件与独立事实标注\n\n评阅者：待填；日期：待填。主要人物完整清单：待填。至少 10 个关键事件：待填。以下 60 个按来源位置等距选择的片段仅帮助覆盖前中后；它们不是人工标注，也不保证覆盖所有关键情节。可结合完整来源补充或替换，保留位置与理由。\n");
        for (var i = 0; i < 60; i++)
        {
            var start = (int)Math.Round(i * Math.Max(0, source.Source.Text.Length - 300) / 59d);
            if (start > 0 && char.IsLowSurrogate(source.Source.Text[start])) start--;
            var end = Math.Min(source.Source.Text.Length, start + 300); if (end < source.Source.Text.Length && char.IsLowSurrogate(source.Source.Text[end])) end--;
            var text = source.Source.Text[start..end]; var section = source.Sections.Last(s => s.Range.Start <= start);
            review.AppendLine($"### 标注 {i + 1:00} · {section.Title} · [{start}, {end})\n")
                .AppendLine("> " + text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\n> ", StringComparison.Ordinal))
                .AppendLine("\n人工确认的事实/人物/事件/规则：待填。\n\n报告是否召回，匹配位置与遗漏：待填。\n");
        }
        review.AppendLine("## 报告核心结论逐项复核\n\n每项填写：有原文支持 / 部分支持 / 不支持 / 无法确认，并说明依据；不能仅凭引用编号存在判为准确。\n");
        var index = 0;
        foreach (var claim in claims)
            review.AppendLine($"### 结论 {++index:00} · {claim.Heading}\n\n{claim.Text}\n\n依据：{string.Join("、", claim.Facts.Select(id => "F" + id))}；模型标记：{claim.Certainty}。\n\n人工判定与错误说明：待填。\n");
        review.AppendLine("## 文风与综合评阅\n\n| 项目 | 认可/不认可 | 原文或报告依据 |\n| --- | --- | --- |\n| 代表性 | 待填 | 待填 |\n| 具体程度 | 待填 | 待填 |\n| 跨阶段差异 | 待填 | 待填 |\n| 维度联系 | 待填 | 待填 |\n| 可读性 | 待填 | 待填 |\n")
            .AppendLine("## 验收统计（必须在评阅后填写）\n\n事实准确性：支持项/受评项，目标至少90%。标注事实召回：命中/有效标注，目标至少85%。主要人物覆盖：目标100%。关键事件召回：目标至少90%。严重人物误合并、结局颠倒或核心矛盾掩盖：不得存在。所有指标当前待评，不是0分，也不是通过。\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "human-review.md"), review.ToString());
        await File.WriteAllTextAsync(Path.Combine(paths.Root, "audit-path.txt"), folder);
        Console.WriteLine(JsonSerializer.Serialize(summary)); return folder;
    }
}
