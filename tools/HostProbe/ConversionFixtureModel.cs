using System.Collections.Immutable;
using System.Text.Json;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;

/// <summary>
/// 只用于本地验收的可控转换器：根据请求中的引用映射返回合法但明确标记为夹具的规范。
/// 不创建网络客户端、不读取凭据，也不把机械转换结果作为真实 AI 的文学质量证据。
/// </summary>
internal sealed class ConversionFixtureModel : ITextModel
{
    public int Requests { get; private set; }
    public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(request.UserPrompt);
        var body = json.RootElement;
        var content = body.GetProperty("Content");
        var material = content.TryGetProperty("Material", out var direct) ? direct : content.GetProperty("SourceBasis");
        var sections = body.GetProperty("SelectedDimensions").EnumerateArray().Select(item =>
        {
            var dimension = Enum.Parse<ProfileDimensions>(item.GetString()!);
            var source = material.EnumerateArray().First(c => Enum.Parse<ProfileDimensions>(c.GetProperty("Targets").GetString()!).HasFlag(dimension));
            var basis = Enum.Parse<TemplateRuleBasis>(source.GetProperty("Basis").GetString()!);
            return new TemplateConversionSection(dimension,
                [new($"验收夹具：在{ReportTemplateDeliveryService.Title(dimension)}中明确角色选择、适用条件和代价。",
                    "隔离工程验收，内容质量尚未评阅", basis, [source.GetProperty("Id").GetInt32()])],
                ["此条目为可控模型返回，不代表 DeepSeek 真实转换质量。"]);
        }).ToImmutableArray();
        Requests++;
        return Task.FromResult(new TextModelResponse(ReportTemplateContract.Serialize(new(sections)), ModelCompletion.Complete, new(100, 120)));
    }
}
