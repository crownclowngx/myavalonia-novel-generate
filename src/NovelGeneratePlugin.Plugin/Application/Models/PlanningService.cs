using System.Text.Json;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

/// <summary>规划是一条结构化文本任务。服务只产生候选，不拿会话或仓库；采用由领域门禁和文档会话负责。</summary>
public sealed class PlanningService(ConnectionService connections, ModelRequestService requests)
{
    public async Task<PlanningCandidate> GenerateAsync(BookProject book, Guid startChapterId, int count, RequestBudget budget, CancellationToken cancellationToken)
    {
        PlanningRules.CheckTargets(book, startChapterId, count);
        var frozen = await connections.FreezeAsync(book, ModelTask.Planning).ConfigureAwait(false);
        var context = new StoryContextBuilder().Build(book, startChapterId, book.Revisions.ActiveRunId, true);
        var contract = new PlanningOutputContract(count); var operationId = Guid.NewGuid();
        var prompt = $"为以下小说从选定章开始规划 {count} 章。只规划，不写正文。远期用主线和分卷目标概括，近期给出具体事件。" +
            "每个字段尽量简洁，以约 80 个汉字表达。Volume 从 1 开始，是本次局部规划的卷序。不要移动已有章节的卷归属。" +
            "实体建议只包括必要的新人物、地点、物品和组织；已有身份不要重建。Kind 使用 Person/Place/Item/Organization。" +
            "如果提供写作方法，每章至少将一条原文方法落实为具体铺设、阻碍、兑现事件，SourceQuote 必须逐字引用方法原文；没有方法时 Methods 可为空。" +
            "StateChanges 是未来规划，不是已发生事实。只返回契约规定的 JSON。";
        var source = context.Render() + "\n\n本次目标章所在既有卷章（只可补计划，不覆盖正文）：\n" +
            JsonSerializer.Serialize(book.Chapters.SkipWhile(c => c.Id != startChapterId).Take(count).Select(c => new { c.Title, Volume = book.Volumes.Single(v => v.Id == c.VolumeId).Title, HasText = !string.IsNullOrWhiteSpace(c.Text) })) +
            "\n\n本书方法原文（本任务必须使用，不受普通检索省略影响）：\n" + book.Profile.Methods;
        var response = await requests.GenerateAsync(new(operationId, frozen, prompt, source, true) { Contract = contract }, budget, null, cancellationToken).ConfigureAwait(false);
        if (response.Completion != ModelCompletion.Complete) throw new InvalidOperationException("规划响应被截断，已保留请求候选，未采用。请调整输出预设后明确重试。");
        var proposal = contract.Parse(response.Text);
        foreach (var chapter in proposal.Chapters) PlanningRules.ValidateMethods(book, chapter.ToPlan());
        return new(book.Id, startChapterId, PlanningRules.SourceStamp(book), operationId, $"{frozen.Connection.Settings.Name} / {frozen.Preset.Model}", proposal);
    }
}

public sealed class PlanningOutputContract(int count) : IModelOutputContract
{
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    public PlanningProposal Parse(string text)
    {
        var proposal = JsonSerializer.Deserialize<PlanningProposal>(text, Options) ?? throw new InvalidDataException("规划对象为空。");
        PlanningRules.ValidateProposal(proposal);
        if (proposal.Chapters.Length != count) throw new InvalidDataException("返回章数与本次选择不一致。");
        return proposal;
    }
    public void Validate(JsonElement value) => Parse(value.GetRawText());
    // 使用明确的对象契约，避免把任意 JSON 或自由文本直接转成作品结构；额外字段在本地同样拒绝。
    public string JsonSchema => """
    {
      "type":"object","additionalProperties":false,"required":["Mainline","Volumes","Chapters","Entities"],
      "properties":{
        "Mainline":{"type":"string"},
        "Volumes":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["Title","Goal"],"properties":{"Title":{"type":"string"},"Goal":{"type":"string"}}}},
        "Entities":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["Name","Kind","Aliases","Description"],"properties":{"Name":{"type":"string"},"Kind":{"type":"string","enum":["Person","Place","Item","Organization"]},"Aliases":{"type":"array","items":{"type":"string"}},"Description":{"type":"string"}}}},
        "Chapters":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["Volume","Title","Goal","Conflict","Viewpoint","TimePlace","Events","StateChanges","Foreshadow","Bridge","Methods"],"properties":{
          "Volume":{"type":"integer"},"Title":{"type":"string"},"Goal":{"type":"string"},"Conflict":{"type":"string"},"Viewpoint":{"type":"string"},"TimePlace":{"type":"string"},"Events":{"type":"string"},"StateChanges":{"type":"string"},"Foreshadow":{"type":"string"},"Bridge":{"type":"string"},
          "Methods":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["SourceQuote","SetupEvent","PressureEvent","PayoffEvent"],"properties":{"SourceQuote":{"type":"string"},"SetupEvent":{"type":"string"},"PressureEvent":{"type":"string"},"PayoffEvent":{"type":"string"}}}}
        }}}
      }
    }
    """;
}
