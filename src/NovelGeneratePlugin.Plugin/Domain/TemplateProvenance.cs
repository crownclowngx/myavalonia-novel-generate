namespace NovelGeneratePlugin.Domain;

/// <summary>
/// 生成来源是模板自己的可选快照，不依赖应用服务或连接对象。模板复制和跨机器导入后仍可说明出处，
/// 详细证据需要原工作区；GeneratedContentHash 记录首次交付内容，用户编辑不会改写这张交付凭据。
/// </summary>
public sealed record TemplateProvenance(Guid ConversionId, Guid BookId, Guid RunId, Guid SourceId,
    string ReportVersion, string SourceHash, string InputHash, string GeneratedContentHash, DateTimeOffset GeneratedAt)
{
    public void Validate()
    {
        if (ConversionId == Guid.Empty || BookId == Guid.Empty || RunId == Guid.Empty || SourceId == Guid.Empty ||
            !Hash(ReportVersion) || !Hash(SourceHash) || !Hash(InputHash) || !Hash(GeneratedContentHash))
            throw new InvalidDataException("模板生成来源的身份或指纹无效。");
    }
    private static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}
