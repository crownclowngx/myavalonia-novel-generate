using System.Collections.Immutable;
namespace NovelGeneratePlugin.Domain;

[Flags]
public enum ProfileDimensions { None = 0, World = 1, Style = 2, Methods = 4, Rules = 8, All = 15 }
public sealed record WritingProfile(string World, string Style, string Methods, string Rules)
{
    public static WritingProfile Empty { get; } = new("", "", "", "");
    public void Validate()
    {
        if (new[] { World, Style, Methods, Rules }.Any(s => s is null || s.Length > 1_000_000))
            throw new InvalidDataException("规范内容不能为空引用，单个维度最长一百万字符。");
    }
    public WritingProfile Merge(WritingProfile source, ProfileDimensions dimensions)
    {
        if (dimensions == ProfileDimensions.None || (dimensions & ~ProfileDimensions.All) != 0) throw new InvalidOperationException("至少选择一个有效模板维度。");
        source.Validate();
        return new WritingProfile(dimensions.HasFlag(ProfileDimensions.World) ? source.World : World,
            dimensions.HasFlag(ProfileDimensions.Style) ? source.Style : Style,
            dimensions.HasFlag(ProfileDimensions.Methods) ? source.Methods : Methods,
            dimensions.HasFlag(ProfileDimensions.Rules) ? source.Rules : Rules);
    }
}
public sealed record TemplateDraft(string Name, ImmutableArray<string> Tags, WritingProfile Content, string Source);
public sealed record TemplateVersion(Guid Id, int Number, string PublishedName, WritingProfile Content, string Source, DateTimeOffset PublishedAt);
public sealed record TemplateAsset(Guid Id, long Revision, TemplateDraft Draft, ImmutableArray<TemplateVersion> Versions, bool Archived)
{
    public static TemplateAsset Create(TemplateDraft draft)
    {
        var asset = new TemplateAsset(Guid.NewGuid(), 0, draft, [], false); asset.Validate(); return asset;
    }
    public TemplateAsset Publish()
    {
        Validate();
        if (Archived) throw new InvalidOperationException("已归档模板不能保存新版本，请先恢复归档。");
        return this with { Versions = Versions.Add(new TemplateVersion(Guid.NewGuid(), Versions.Length + 1, Draft.Name, Draft.Content, Draft.Source, DateTimeOffset.UtcNow)) };
    }
    public void Validate()
    {
        if (Id == Guid.Empty || Revision < 0 || Draft is null || string.IsNullOrWhiteSpace(Draft.Name) || Draft.Name.Length > 120 ||
            Draft.Tags.IsDefault || Draft.Tags.Length > 30 || Draft.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50) || Draft.Source is null || Versions.IsDefault)
            throw new InvalidDataException("模板身份、名称、标签或版本无效。");
        if (Draft.Content is null) throw new InvalidDataException("模板规范不能为空。");
        Draft.Content.Validate(); var ids = new HashSet<Guid>();
        for (var i = 0; i < Versions.Length; i++)
        {
            var version = Versions[i];
            if (version is null || version.Id == Guid.Empty || !ids.Add(version.Id) || version.Number != i + 1 || version.Content is null || version.PublishedName is null || version.Source is null)
                throw new InvalidDataException("模板版本序号或身份无效。");
            version.Content.Validate();
        }
    }
}

/// <summary>采用时保存完整来源内容和选择维度；作品规范继续独立编辑，库改名/归档/升级不会改变它。</summary>
public sealed record TemplateAdoption(Guid TemplateId, Guid VersionId, int VersionNumber, string Name,
    WritingProfile SourceContent, ProfileDimensions Dimensions, DateTimeOffset AdoptedAt);

public static class TemplateAdoptionRules
{
    public static BookProject Adopt(BookProject book, TemplateAsset template, Guid versionId, ProfileDimensions dimensions)
    {
        book.Validate(); template.Validate();
        if (template.Archived) throw new InvalidOperationException("此模板已归档，不能用于新的采用操作。");
        var version = template.Versions.Single(v => v.Id == versionId);
        return book with
        {
            Profile = book.Profile.Merge(version.Content, dimensions),
            AdoptedTemplate = new TemplateAdoption(template.Id, version.Id, version.Number, template.Draft.Name, version.Content, dimensions, DateTimeOffset.UtcNow)
        };
    }
}
