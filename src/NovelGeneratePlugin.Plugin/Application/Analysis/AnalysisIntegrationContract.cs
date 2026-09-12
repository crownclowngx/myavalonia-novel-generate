using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record IdentityGroup(string Name, StoryEntityKind Category, ImmutableArray<int> Mentions, AnalysisStatementKind Certainty, string Reason);
public sealed record IdentityOutput(ImmutableArray<IdentityGroup> Groups, ImmutableArray<string> OpenQuestions);
public enum ContinuityRole { Rule, Exception, Conflict, Event, Relationship, Goal, Obstacle, Action, Outcome, MainPlot, Subplot, Foreshadow, Unresolved, Style, Theme, Contradiction }
public sealed record ContinuityObservation(string Subject, string Statement, AnalysisStatementKind Certainty, NarrativeSource Narration,
    string StoryTime, ContinuityRole Role, string Conditions, string Exceptions, ImmutableArray<int> Facts);
public sealed record ContinuityOutput(ImmutableArray<ContinuityObservation> Observations, ImmutableArray<string> OpenQuestions);

/// <summary>整合结果只引用已有提及与事实，不接受模型生成原文坐标。未知身份分组作为候选保留，不能当成同一人的自动归并。</summary>
public sealed class IdentityAnalysisContract(IReadOnlyList<IndexedMention> input) : IModelOutputContract
{
    public void Validate(JsonElement value) => Read(value.GetRawText());
    public IdentityOutput Read(string json)
    {
        var output = AnalysisJson.Read<IdentityOutput>(json); var ids = input.ToDictionary(m => m.Number); var seen = new HashSet<int>();
        if (output.Groups.IsDefault || output.Groups.Length > 80) throw new InvalidDataException("身份分组超过容量。");
        AnalysisJson.Questions(output.OpenQuestions);
        foreach (var group in output.Groups)
        {
            if (group is null || !Enum.IsDefined(group.Category) || !Enum.IsDefined(group.Certainty)) throw new InvalidDataException("身份判断类型无效。");
            AnalysisJson.Text(group.Name, 100); AnalysisJson.Text(group.Reason, 1000);
            AnalysisJson.References(group.Mentions, ids.Keys, 80);
            foreach (var id in group.Mentions)
                if (!seen.Add(id) || ids[id].Value.Kind != group.Category) throw new InvalidDataException("身份提及被重复归组或跨实体类别混合。");
        }
        // 未覆盖的提及以本地显式未知单例保留，绝不因模型遗漏而从全书人物表中消失。
        var unassigned = input.Where(m => !seen.Contains(m.Number)).Select(m => new IdentityGroup(m.Value.Name, m.Value.Kind, [m.Number], AnalysisStatementKind.Uncertain, "此轮未作身份判断，保留独立提及。"));
        return output with { Groups = [.. output.Groups, .. unassigned] };
    }
    public string JsonSchema => AnalysisJson.Schema(new()
    {
        ["Groups"] = AnalysisJson.Array(AnalysisJson.Object(new() { ["Name"] = AnalysisJson.String, ["Category"] = AnalysisJson.Enum<StoryEntityKind>(), ["Mentions"] = AnalysisJson.Array(AnalysisJson.Integer), ["Certainty"] = AnalysisJson.Enum<AnalysisStatementKind>(), ["Reason"] = AnalysisJson.String })),
        ["OpenQuestions"] = AnalysisJson.Array(AnalysisJson.String)
    });
}

/// <summary>保留规则条件、例外、矛盾与叙述时间。事实引用可证明来源链，语义支持仍需质量评阅，不能用结构校验冒充准确率。</summary>
public sealed class ContinuityAnalysisContract(IReadOnlyList<IndexedFinding> input) : IModelOutputContract
{
    public void Validate(JsonElement value) => Read(value.GetRawText());
    public ContinuityOutput Read(string json)
    {
        var output = AnalysisJson.Read<ContinuityOutput>(json); var ids = input.ToDictionary(f => f.Number);
        if (output.Observations.IsDefault || output.Observations.Length > 40) throw new InvalidDataException("整合观察超过容量。");
        AnalysisJson.Questions(output.OpenQuestions);
        foreach (var observation in output.Observations)
        {
            if (observation is null || !Enum.IsDefined(observation.Certainty) || !Enum.IsDefined(observation.Narration) || !Enum.IsDefined(observation.Role)) throw new InvalidDataException("整合观察类型无效。");
            AnalysisJson.Text(observation.Subject, 200); AnalysisJson.Text(observation.Statement, 2000);
            AnalysisJson.Text(observation.StoryTime, 500, true); AnalysisJson.Text(observation.Conditions, 1000, true); AnalysisJson.Text(observation.Exceptions, 1000, true);
            AnalysisJson.References(observation.Facts, ids.Keys, 16);
            var facts = observation.Facts.Select(id => ids[id].Value).ToArray();
            // 故事中的价值/行动冲突可以明确存在；只有无法消解的事实矛盾必须保留不确定性，两者不能共用一个判断。
            if (observation.Role == ContinuityRole.Contradiction && observation.Certainty != AnalysisStatementKind.Uncertain ||
                observation.Certainty == AnalysisStatementKind.Explicit && facts.All(f => f.Kind != AnalysisStatementKind.Explicit) ||
                observation.Certainty == AnalysisStatementKind.Explicit && observation.Narration == NarrativeSource.Narration &&
                facts.Any(f => f.Kind == AnalysisStatementKind.Uncertain || f.Narration is NarrativeSource.CharacterClaim or NarrativeSource.Rumor or NarrativeSource.Dream or NarrativeSource.Plan))
                throw new InvalidDataException("不能把矛盾、梦境、传闻或未证实的角色说法提升为确定客观事实。");
        }
        return output;
    }
    public string JsonSchema => AnalysisJson.Schema(new()
    {
        ["Observations"] = AnalysisJson.Array(AnalysisJson.Object(new()
        {
            ["Subject"] = AnalysisJson.String,
            ["Statement"] = AnalysisJson.String,
            ["Certainty"] = AnalysisJson.Enum<AnalysisStatementKind>(),
            ["Narration"] = AnalysisJson.Enum<NarrativeSource>(),
            ["StoryTime"] = AnalysisJson.String,
            ["Role"] = AnalysisJson.Enum<ContinuityRole>(),
            ["Conditions"] = AnalysisJson.String,
            ["Exceptions"] = AnalysisJson.String,
            ["Facts"] = AnalysisJson.Array(AnalysisJson.Integer)
        })),
        ["OpenQuestions"] = AnalysisJson.Array(AnalysisJson.String)
    });
}

/// <summary>分析契约共用严格 JSON 与边界校验，不承担任务调度或模型调用。</summary>
internal static class AnalysisJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("分析响应为空。");
    public static void Text(string value, int maximum, bool empty = false)
    { if (value is null || !empty && string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new InvalidDataException("分析文本字段缺失或超过容量。"); }
    public static void Questions(ImmutableArray<string> questions)
    { if (questions.IsDefault || questions.Length > 30) throw new InvalidDataException("问题清单超过容量。"); foreach (var question in questions) Text(question, 1000); }
    public static void References(ImmutableArray<int> references, IEnumerable<int> allowed, int maximum)
    {
        var ids = allowed.ToHashSet();
        if (references.IsDefaultOrEmpty || references.Length > maximum || references.Distinct().Count() != references.Length || references.Any(id => !ids.Contains(id)))
            throw new InvalidDataException("整合引用重复、越界或不属于输入。");
    }
    public static object String => new { type = "string" };
    public static object Integer => new { type = "integer" };
    public static object Enum<T>() where T : struct, Enum => new { type = "string", @enum = System.Enum.GetNames<T>() };
    public static object Array(object items) => new { type = "array", items };
    public static object Object(Dictionary<string, object> properties) => new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
    public static string Schema(Dictionary<string, object> properties) => Write(Object(properties));
}
