using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>
/// 分析参数与连接授权分开：连接仍冻结真实已保存身份，任务只覆盖生成参数，不复制或伪造连接配置。
/// 旧运行的 Settings 为 null，继续使用原检查预设，原有输入指纹保持不变。
/// </summary>
public sealed record AnalysisStageSettings(ModelPreset Extraction, ModelPreset Integration, ModelPreset Summary, ModelPreset Dimension, ModelPreset Synthesis)
{
    public ModelPreset For(AnalysisNodeKind kind) => kind switch
    {
        AnalysisNodeKind.Extraction => Extraction,
        AnalysisNodeKind.Integration => Integration,
        AnalysisNodeKind.Summary => Summary,
        AnalysisNodeKind.Dimension => Dimension,
        AnalysisNodeKind.Synthesis => Synthesis,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static AnalysisStageSettings Default(FrozenConnection connection)
    {
        var preset = connection.Preset;
        if (connection.Connection.Settings.Provider != ModelProvider.DeepSeek) return new(preset, preset, preset, preset, preset);
        var extraction = new ModelPreset(preset.Model, 16384, "none"); var reasoning = new ModelPreset(preset.Model, 32768, "high");
        return new(extraction, reasoning, extraction, reasoning, reasoning);
    }
    public void Validate(ModelProvider provider)
    {
        foreach (var kind in Enum.GetValues<AnalysisNodeKind>())
        {
            var preset = For(kind) ?? throw new InvalidDataException("分析阶段参数缺失。"); preset.Validate();
            if (provider == ModelProvider.CodexCli && preset.ReasoningEffort is "none" or "max")
                throw new InvalidDataException("当前 Codex 分析使用 low/medium/high 思考强度。");
        }
    }
}
