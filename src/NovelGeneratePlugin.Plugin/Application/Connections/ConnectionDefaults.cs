using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Connections;

/// <summary>
/// 新连接的产品默认值，集中记录而不写入用户已有连接。
/// DeepSeek 参数依据 2026-09-12 官方 Chat Completions 文档：思考默认 high、输出默认 64K。
/// 仅提供可编辑草案；不自动保存、不探测网络、不读取或预置 API Key。
/// </summary>
public static class ConnectionDefaults
{
    public static ConnectionSettings Create(ModelProvider provider)
    {
        var preset = provider == ModelProvider.DeepSeek
            ? new ModelPreset("deepseek-flash", 65536, "high")
            : new ModelPreset("gpt-6-astra", 8192, "high");
        return new(provider == ModelProvider.DeepSeek ? "DeepSeek 默认连接" : "Codex 开发验证", provider,
            provider == ModelProvider.DeepSeek ? "https://api.deepseek.com" : "", "", preset, preset, preset);
    }
    public static IReadOnlyList<string> Models(ModelProvider provider) => provider == ModelProvider.DeepSeek
        ? ["deepseek-flash", "deepseek-v4-pro"] : ["gpt-6-astra"];
    public static IReadOnlyList<string> Efforts(ModelProvider provider) => provider == ModelProvider.DeepSeek
        ? ["none", "low", "high", "max"] : ["low", "medium", "high"];
}
