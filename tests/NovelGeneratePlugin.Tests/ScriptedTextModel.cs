using NovelGeneratePlugin.Application.Models;
namespace NovelGeneratePlugin.Tests;

/// <summary>测试专用模型替身：后续调度用例可以按请求顺序注入完整、截断或异常；生产项目不引用此类。</summary>
internal sealed class ScriptedTextModel(params Func<TextModelRequest, TextModelResponse>[] steps) : ITextModel
{
    private readonly Queue<Func<TextModelRequest, TextModelResponse>> _steps = new(steps);
    public List<TextModelRequest> Requests { get; } = [];
    public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
        if (_steps.Count == 0) throw new InvalidOperationException("模型替身没有剩余响应，不能默默返回虚构成功。");
        var response = _steps.Dequeue()(request); progress?.Report(response.Text); return Task.FromResult(response);
    }
}
