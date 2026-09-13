using CommunityToolkit.Mvvm.ComponentModel;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Features.TemplateLibrary;

/// <summary>仅编辑分析参数，不写连接库；用户建立运行或修订时由应用层冻结并验证。</summary>
public sealed partial class AnalysisStageEditor : ObservableObject
{
    public AnalysisNodeKind Kind { get; }
    public string Name { get; }
    [ObservableProperty] private string _model;
    [ObservableProperty] private int _maximumOutput;
    [ObservableProperty] private string _reasoning;
    public IReadOnlyList<string> ReasoningOptions { get; } = ["none", "low", "medium", "high", "max"];
    public AnalysisStageEditor(AnalysisNodeKind kind, ModelPreset preset)
    {
        Kind = kind; Name = kind switch { AnalysisNodeKind.Extraction => "全文提取", AnalysisNodeKind.Integration => "跨章整合", AnalysisNodeKind.Summary => "分层汇总", AnalysisNodeKind.Dimension => "专题分析", _ => "综合结论" };
        _model = preset.Model; _maximumOutput = preset.MaxOutputTokens; _reasoning = preset.ReasoningEffort;
    }
    public ModelPreset Build() => new(Model, MaximumOutput, Reasoning);
}
