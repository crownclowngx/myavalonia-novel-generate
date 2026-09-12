using System.Collections.Immutable;
namespace NovelGeneratePlugin.Domain;

public enum MaterialPurpose { Methods, Style }
public sealed record MaterialSegment(int Number, int Start, int Length, string Text);
public sealed record MaterialEvidence(int Segment, string Quote, bool Inferred);
public sealed record MethodCard(string Name, string Condition, ImmutableArray<string> Steps, string CounterExample, string Conflict, ImmutableArray<MaterialEvidence> Evidence);
public sealed record StyleDimension(string Name, string Instruction, string PositiveExample, string NegativeExample, ImmutableArray<MaterialEvidence> Evidence);
public sealed record MaterialAnalysis(ImmutableArray<MethodCard> Methods, ImmutableArray<StyleDimension> Style);
public sealed record TrialEvidence(string Constraint, string EvidenceA, string EvidenceB);
public sealed record MaterialTrial(string Goal, string Setup, string Pressure, string Payoff, string MethodQuote, string SampleA, string SampleB, ImmutableArray<TrialEvidence> Evidence);
public sealed record MaterialDocument(Guid Id, long Version, string Name, MaterialPurpose Purpose, string Source, string Text, int AnalyzeCharacters,
    string Scene, string Constraints, string Methods, string Style, string Feedback, string ChosenSample, ConnectionBinding? Connection,
    MaterialAnalysis? Analysis, string AnalysisStamp, MaterialTrial? Trial, string TrialStamp, ImmutableArray<Guid> Budgets)
{
    public static MaterialDocument Create() => new(Guid.NewGuid(), 0, "新材料", MaterialPurpose.Methods, "作者粘贴", "", 12000, "邮差在旧门前发现一把钥匙。", "只有邮差一人出场\n本场景没有获得钥匙的归属信息", "", "", "", "", null, null, "", null, "", []);
    public string SourceStamp => CanonicalJson.Hash(new { Purpose, Source, Text, AnalyzeCharacters });
    public string SampleStamp => CanonicalJson.Hash(new { Scene, Constraints, Methods, Style });
    public void Validate()
    {
        if (Id == Guid.Empty || Version < 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || !Enum.IsDefined(Purpose) || Source is null || Source.Length > 2000 || Text is null || Text.Length > 20000 ||
            AnalyzeCharacters is < 1 or > 12000 || new[] { Scene, Constraints, Methods, Style, Feedback, ChosenSample, AnalysisStamp, TrialStamp }.Any(t => t is null || t.Length > 30000) || Budgets.IsDefault)
            throw new InvalidDataException("短材料草案无效：全文最多 20000 字符，单次处理最多 12000 字符。");
    }
    public ImmutableArray<MaterialSegment> Segments()
    {
        var count = Math.Min(Text.Length, AnalyzeCharacters); var result = ImmutableArray.CreateBuilder<MaterialSegment>();
        for (var start = 0; start < count; start += 800) { var length = Math.Min(800, count - start); result.Add(new(result.Count + 1, start, length, Text.Substring(start, length))); }
        return result.ToImmutable();
    }
}
public sealed record MaterialAdoption(Guid MaterialId, long Version, string Name, string Source, string Methods, string Style, string Feedback, string ChosenSample, DateTimeOffset AdoptedAt);

/// <summary>来源证据只在被处理的材料片段中定位；未处理部分不能被模型引用为已分析依据。</summary>
public static class MaterialRules
{
    public static void ValidateAnalysis(MaterialDocument material, MaterialAnalysis analysis)
    {
        material.Validate();
        if (analysis is null || analysis.Methods.IsDefault || analysis.Style.IsDefault || analysis.Methods.Length > 12 || analysis.Style.Length > 12 ||
            material.Purpose == MaterialPurpose.Methods && analysis.Methods.IsEmpty || material.Purpose == MaterialPurpose.Style && analysis.Style.IsEmpty)
            throw new InvalidDataException("提炼结果缺少所选用途的方法卡或文风维度。");
        void Evidence(ImmutableArray<MaterialEvidence> evidence)
        {
            if (evidence.IsDefaultOrEmpty || evidence.Length > 6) throw new InvalidDataException("每张卡片需要 1–6 条原文依据。");
            foreach (var item in evidence)
            {
                if (item is null) throw new InvalidDataException("材料证据为空。");
                var segment = material.Segments().SingleOrDefault(s => s.Number == item.Segment);
                if (item is null || string.IsNullOrWhiteSpace(item.Quote) || item.Quote.Length > 1000 || segment is null || !segment.Text.Contains(item.Quote, StringComparison.Ordinal))
                    throw new InvalidDataException("材料证据不在已处理片段中。");
            }
        }
        foreach (var card in analysis.Methods)
        {
            if (card is null || new[] { card.Name, card.Condition, card.CounterExample, card.Conflict }.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 2000) || card.Steps.IsDefaultOrEmpty || card.Steps.Length > 10 || card.Steps.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 2000)) throw new InvalidDataException("方法卡需要条件、步骤、反例和冲突说明。");
            Evidence(card.Evidence);
        }
        foreach (var dimension in analysis.Style)
        {
            if (dimension is null || new[] { dimension.Name, dimension.Instruction, dimension.PositiveExample, dimension.NegativeExample }.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 2000)) throw new InvalidDataException("文风维度需要可操作规则和正反例。");
            Evidence(dimension.Evidence);
        }
        if (MethodsText(analysis).Length > 30000 || StyleText(analysis).Length > 30000) throw new InvalidDataException("提炼后的规范超过可保存容量，请减少卡片或缩短内容。");
    }
    public static string MethodsText(MaterialAnalysis analysis) => string.Join("\n\n", analysis.Methods.Select(c => $"方法：{c.Name}\n条件：{c.Condition}\n步骤：{string.Join("；", c.Steps)}\n反例：{c.CounterExample}\n冲突：{c.Conflict}\n" + EvidenceText(c.Evidence)));
    public static string StyleText(MaterialAnalysis analysis) => string.Join("\n\n", analysis.Style.Select(c => $"维度：{c.Name}\n写法：{c.Instruction}\n正例：{c.PositiveExample}\n反例：{c.NegativeExample}\n" + EvidenceText(c.Evidence)));
    private static string EvidenceText(ImmutableArray<MaterialEvidence> evidence) => string.Join('\n', evidence.Select(e => $"来源片段 {e.Segment}（{(e.Inferred ? "由样本推断" : "直接支持")}）：{e.Quote}"));
    public static void ValidateTrial(MaterialDocument material, MaterialTrial trial)
    {
        if (trial is null || new[] { trial.Goal, trial.Setup, trial.Pressure, trial.Payoff, trial.SampleA, trial.SampleB }.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 4000) || trial.MethodQuote is null ||
            trial.Evidence.IsDefault || trial.Evidence.Length > 20 || trial.Evidence.Any(e => e is null || e.Constraint is null || e.EvidenceA is null || e.EvidenceB is null)) throw new InvalidDataException("试写需要具体剧情小样、两篇正文和约束证据。");
        if (!string.IsNullOrWhiteSpace(material.Methods) && (string.IsNullOrWhiteSpace(trial.MethodQuote) || !material.Methods.Contains(trial.MethodQuote, StringComparison.Ordinal))) throw new InvalidDataException("剧情小样的方法来源不在当前方法文本中。");
        var constraints = material.Constraints.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (constraints.Length == 0 || constraints.Length > 20 || trial.Evidence.Length != constraints.Length || trial.Evidence.Select(e => e.Constraint).Distinct().Count() != constraints.Length) throw new InvalidDataException("试写必须逐条对应同一组事实约束。");
        foreach (var evidence in trial.Evidence)
            if (evidence is null || !constraints.Contains(evidence.Constraint) || string.IsNullOrWhiteSpace(evidence.EvidenceA) || string.IsNullOrWhiteSpace(evidence.EvidenceB) ||
                !trial.SampleA.Contains(evidence.EvidenceA, StringComparison.Ordinal) || !trial.SampleB.Contains(evidence.EvidenceB, StringComparison.Ordinal)) throw new InvalidDataException("对比样稿的事实约束证据缺失。");
    }
    public static MaterialAdoption Adoption(MaterialDocument material)
    {
        material.Validate(); if (material.Analysis is null || material.SourceStamp != material.AnalysisStamp) throw new InvalidOperationException("材料尚未提炼或来源已变化，请重新提炼。");
        ValidateAnalysis(material, material.Analysis);
        if (string.IsNullOrWhiteSpace(material.Methods) && string.IsNullOrWhiteSpace(material.Style)) throw new InvalidOperationException("没有可采用的方法或文风。");
        return new(material.Id, material.Version, material.Name, material.Source, material.Methods, material.Style, material.Feedback, material.ChosenSample, DateTimeOffset.UtcNow);
    }
    public static BookProject Adopt(BookProject book, MaterialDocument material)
    {
        var source = Adoption(material); var profile = book.Profile with
        {
            Methods = string.IsNullOrWhiteSpace(source.Methods) ? book.Profile.Methods : source.Methods,
            Style = string.IsNullOrWhiteSpace(source.Style) ? book.Profile.Style : source.Style
        };
        var result = book with { Profile = profile, MaterialSources = book.MaterialSources.Add(source) }; result.Validate(); return result;
    }
}
