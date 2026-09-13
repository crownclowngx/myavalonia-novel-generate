using System.Text.Json;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Templates;

/// <summary>
/// 模板规范采用短条目，来源引用只能落在冻结报告内。Schema 与本地校验共用容量常量；
/// 模型将推断提升为明确事实时也会拒绝，避免原分析的不确定性在转换后消失。
/// </summary>
public sealed class ReportTemplateContract(ProfileDimensions dimensions, IReadOnlyList<ReportTemplateClaim> available) : IModelOutputContract
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public void Validate(JsonElement value) => Read(value.GetRawText());
    public TemplateConversionCandidate Read(string json)
    {
        Length(json, "$", 1, ConversionLimits.JsonCharacters);
        var candidate = JsonSerializer.Deserialize<TemplateConversionCandidate>(json, Json) ?? throw new JsonException("模板候选为空。");
        ValidateCandidate(candidate); return candidate;
    }
    public static string Serialize(TemplateConversionCandidate candidate) => JsonSerializer.Serialize(candidate, Json);
    public void ValidateCandidate(TemplateConversionCandidate candidate)
    {
        var selected = ConversionLimits.Selected(dimensions);
        Count(candidate.Sections.IsDefault ? -1 : candidate.Sections.Length, "$.Sections", selected.Length, selected.Length);
        var seen = new HashSet<ProfileDimensions>(); var sources = available.ToDictionary(c => c.Id);
        for (var i = 0; i < candidate.Sections.Length; i++)
        {
            var part = candidate.Sections[i]; var path = $"$.Sections[{i}]";
            if (part is null) Fail(path, ModelContractRule.Required);
            if (!selected.Contains(part!.Dimension) || !seen.Add(part.Dimension)) Fail(path + ".Dimension", ModelContractRule.EnumValue);
            Count(part.Rules.IsDefault ? -1 : part.Rules.Length, path + ".Rules", 0, ConversionLimits.RulesPerSection);
            Count(part.Questions.IsDefault ? -1 : part.Questions.Length, path + ".Questions", 0, ConversionLimits.QuestionsPerSection);
            if (part.Rules.IsEmpty && part.Questions.IsEmpty) Fail(path + ".Rules", ModelContractRule.Required);
            for (var j = 0; j < part.Rules.Length; j++)
            {
                var rule = part.Rules[j]; var field = path + $".Rules[{j}]";
                if (rule is null) Fail(field, ModelContractRule.Required);
                Length(rule!.Text, field + ".Text", 1, ConversionLimits.RuleCharacters);
                Length(rule.Condition, field + ".Condition", 0, ConversionLimits.ConditionCharacters);
                if (!Enum.IsDefined(rule.Basis)) Fail(field + ".Basis", ModelContractRule.EnumValue);
                Count(rule.Sources.IsDefault ? -1 : rule.Sources.Length, field + ".Sources", rule.Basis == TemplateRuleBasis.Suggestion ? 0 : 1, ConversionLimits.SourcesPerRule);
                var used = new HashSet<int>();
                for (var k = 0; k < rule.Sources.Length; k++)
                    if (!sources.TryGetValue(rule.Sources[k], out var source) || !source.Targets.HasFlag(part.Dimension) || !used.Add(rule.Sources[k]))
                        Fail(field + $".Sources[{k}]", ModelContractRule.EnumValue);
                if (rule.Basis == TemplateRuleBasis.Supported && rule.Sources.Any(id => sources[id].Basis != TemplateRuleBasis.Supported) ||
                    rule.Basis == TemplateRuleBasis.Inferred && rule.Sources.Any(id => sources[id].Basis == TemplateRuleBasis.Suggestion))
                    Fail(field + ".Basis", ModelContractRule.EnumValue);
            }
            for (var j = 0; j < part.Questions.Length; j++) Length(part.Questions[j], path + $".Questions[{j}]", 1, ConversionLimits.QuestionCharacters);
        }
    }
    private static void Fail(string path, ModelContractRule rule) => throw new ModelContractException(path, new(rule));
    private static void Count(int actual, string path, int minimum, int maximum)
    { if (actual < minimum || actual > maximum) throw new ModelContractException(path, new(ModelContractRule.ArrayCount, actual, minimum, maximum)); }
    private static void Length(string? text, string path, int minimum, int maximum)
    {
        if (text is null || minimum > 0 && string.IsNullOrWhiteSpace(text)) Fail(path, ModelContractRule.Required);
        if (text!.Length < minimum || text.Length > maximum) throw new ModelContractException(path, new(ModelContractRule.TextLength, text.Length, minimum, maximum));
    }
    private static object Text(int maximum) => new { type = "string", maxLength = maximum };
    private static object Array(object item, int minimum, int maximum) => new { type = "array", items = item, minItems = minimum, maxItems = maximum };
    private static object Object(Dictionary<string, object> fields) => new { type = "object", additionalProperties = false, properties = fields, required = fields.Keys.ToArray() };
    public string JsonSchema => JsonSerializer.Serialize(Object(new()
    {
        ["Sections"] = Array(Object(new()
        {
            ["Dimension"] = new { type = "string", @enum = ConversionLimits.Selected(dimensions).Select(d => d.ToString()).ToArray() },
            ["Rules"] = Array(Object(new()
            {
                ["Text"] = new { type = "string", minLength = 1, maxLength = ConversionLimits.RuleCharacters },
                ["Condition"] = Text(ConversionLimits.ConditionCharacters),
                ["Basis"] = new { type = "string", @enum = Enum.GetNames<TemplateRuleBasis>() },
                ["Sources"] = Array(new { type = "integer", @enum = available.Select(c => c.Id).ToArray() }, 0, ConversionLimits.SourcesPerRule)
            }), 0, ConversionLimits.RulesPerSection),
            ["Questions"] = Array(new { type = "string", minLength = 1, maxLength = ConversionLimits.QuestionCharacters }, 0, ConversionLimits.QuestionsPerSection)
        }), ConversionLimits.Selected(dimensions).Length, ConversionLimits.Selected(dimensions).Length)
    }));
}
