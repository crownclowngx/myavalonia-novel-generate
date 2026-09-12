using System.Text.Json;
namespace NovelGeneratePlugin.Application.Models;

public sealed record SelectionEdit(int Start, int Length, string Instruction, bool Append);
public sealed record SelectionReplacement(string Replacement);
/// <summary>模型只能返回选区内容；不允许返回位置或多个补丁以改变作者授权的范围。</summary>
public sealed class SelectionReplacementContract : IModelOutputContract
{
    public SelectionReplacement Parse(string text)
    {
        var result = JsonSerializer.Deserialize<SelectionReplacement>(text, ChapterReviewContract.Options);
        if (result?.Replacement is null || result.Replacement.Length > 20000) throw new InvalidDataException("替换文字为空或超出容量。");
        return result;
    }
    public void Validate(JsonElement value) => Parse(value.GetRawText());
    public string JsonSchema => """
        {"type":"object","additionalProperties":false,"required":["Replacement"],"properties":{"Replacement":{"type":"string","maxLength":20000}}}
        """;
}
