using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Templates;

public interface ITemplateStore
{
    IReadOnlyList<TemplateAsset> List();
    TemplateAsset Read(Guid id);
    TemplateAsset Save(TemplateAsset asset, long? expectedRevision);
    IReadOnlyList<TemplateRecoveryEntry> ListRecovery();
    TemplateDraftRecovery ReadRecovery(Guid id);
    void WriteRecovery(TemplateDraftRecovery recovery);
    void DeleteRecovery(Guid id);
}
public sealed record TemplateRecoveryEntry(Guid Id, string Name, bool CanRead)
{ public override string ToString() => Name + (CanRead ? " · 待恢复草案" : " · 无法读取"); }
public sealed record TemplateDraftRecovery(Guid Id, int FormatVersion, TemplateAsset Asset, long EditGeneration)
{ public override string ToString() => Asset.Draft.Name + " · 待恢复草案"; }
public sealed record TemplateChoice(Guid TemplateId, Guid VersionId, int VersionNumber, string Name)
{
    public ProfileDimensions AvailableDimensions { get; init; } = ProfileDimensions.All;
    public override string ToString() => $"{Name} · v{VersionNumber}";
}

/// <summary>模板用例与界面解耦；Document 可直接查询和采用已保存版本，不依赖 Tool 是否创建。</summary>
public sealed class TemplateLibrary(ITemplateStore store)
{
    public event Action? VersionsChanged;
    private void NotifyVersions()
    { foreach (var handler in VersionsChanged?.GetInvocationList() ?? []) try { ((Action)handler)(); } catch (Exception) { /* 列表订阅失败不能撤销已保存版本。 */ } }
    public Task<IReadOnlyList<TemplateAsset>> ListAsync() => Task.Run(store.List);
    public Task<TemplateAsset> ReadAsync(Guid id) => Task.Run(() => store.Read(id));
    public Task<TemplateAsset> CreateAsync(TemplateDraft draft) => Task.Run(() => store.Save(TemplateAsset.Create(draft), null));
    /// <summary>本书另存模板是一次创建操作；先在内存产生 v1，再一次提交，避免两次落盘之间留下半成品。</summary>
    public async Task<TemplateAsset> CreatePublishedAsync(TemplateDraft draft)
    { var saved = await Task.Run(() => store.Save(TemplateAsset.Create(draft).Publish(), null)).ConfigureAwait(false); NotifyVersions(); return saved; }
    /// <summary>
    /// 生成任务在发送前已分配目标身份。首次插入失败后只核对同一身份的首次交付凭据；
    /// 后续用户编辑使内容不同也不会再次写入，避免“恢复”把编辑后的草案覆盖成原 AI 候选。
    /// </summary>
    public Task<TemplateAsset> CreateGeneratedDraftAsync(Guid templateId, TemplateDraft draft) => Task.Run(() =>
    {
        if (templateId == Guid.Empty || draft.Provenance is null) throw new InvalidDataException("生成草案缺少目标身份或来源凭据。");
        draft.Provenance.Validate();
        if (draft.Provenance.GeneratedContentHash != CanonicalJson.Hash(draft.Content)) throw new InvalidDataException("首次生成内容与交付指纹不一致。");
        var asset = TemplateAsset.Create(draft) with { Id = templateId };
        try { return store.Save(asset, null); }
        catch (Exception writeError) when (writeError is not OutOfMemoryException)
        {
            TemplateAsset existing;
            try { existing = store.Read(templateId); }
            catch (Exception readError) when (readError is not OutOfMemoryException)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writeError).Throw(); throw; }
            if (existing.Draft.Provenance != draft.Provenance) throw new InvalidDataException("目标模板身份已被其他来源使用，未覆盖。");
            return existing;
        }
    });
    public Task<TemplateAsset> SaveDraftAsync(TemplateAsset current, TemplateDraft draft)
    {
        if (current.Archived) throw new InvalidOperationException("已归档模板不能编辑。");
        return Task.Run(() => store.Save(current with { Draft = draft }, current.Revision));
    }
    public async Task<TemplateAsset> PublishAsync(TemplateAsset current)
    { var saved = await Task.Run(() => store.Save(current.Publish(), current.Revision)).ConfigureAwait(false); NotifyVersions(); return saved; }
    public async Task<TemplateAsset> ArchiveAsync(TemplateAsset current, bool archived)
    { var saved = await Task.Run(() => store.Save(current with { Archived = archived }, current.Revision)).ConfigureAwait(false); NotifyVersions(); return saved; }
    public Task<IReadOnlyList<TemplateRecoveryEntry>> ListRecoveryAsync() => Task.Run(store.ListRecovery);
    public Task<TemplateDraftRecovery> ReadRecoveryAsync(Guid id) => Task.Run(() => store.ReadRecovery(id));
    public Task WriteRecoveryAsync(TemplateDraftRecovery recovery) => Task.Run(() => store.WriteRecovery(recovery));
    public Task DeleteRecoveryAsync(Guid id) => Task.Run(() => store.DeleteRecovery(id));
    public async Task<TemplateAsset> RestoreAsCopyAsync(Guid id)
    {
        var recovery = await ReadRecoveryAsync(id).ConfigureAwait(false);
        var source = recovery.Asset;
        var restored = source with
        {
            Id = Guid.NewGuid(),
            Revision = 0,
            Archived = false,
            Draft = source.Draft with { Name = source.Draft.Name[..Math.Min(source.Draft.Name.Length, 112)] + " 恢复副本" },
            Versions = System.Collections.Immutable.ImmutableArray.CreateRange(source.Versions.Select(v => v with { Id = Guid.NewGuid() }))
        };
        var saved = await Task.Run(() => store.Save(restored, null)).ConfigureAwait(false);
        // 原恢复文件保留，避免恢复后清理失败使已创建的新模板被误报成失败。
        return saved;
    }
    public Task<TemplateAsset> CopyAsync(TemplateAsset source) => CreateAsync(source.Draft with { Name = source.Draft.Name[..Math.Min(source.Draft.Name.Length, 115)] + " 副本" });
    public Task<TemplateAsset> ImportCopyAsync(TemplateAsset source)
    {
        source.Validate();
        var imported = source with
        {
            Id = Guid.NewGuid(),
            Revision = 0,
            Archived = false,
            Versions = System.Collections.Immutable.ImmutableArray.CreateRange(source.Versions.Select(v => v with { Id = Guid.NewGuid() }))
        };
        return Task.Run(() => store.Save(imported, null));
    }
    public async Task<IReadOnlyList<TemplateChoice>> ChoicesAsync()
    {
        var assets = await ListAsync().ConfigureAwait(false);
        return assets.Where(a => !a.Archived).SelectMany(a => a.Versions.Reverse().Select(v => new TemplateChoice(a.Id, v.Id, v.Number, a.Draft.Name)
        { AvailableDimensions = Available(v.Content) })).ToArray();
    }
    private static ProfileDimensions Available(WritingProfile profile) =>
        (string.IsNullOrWhiteSpace(profile.World) ? 0 : ProfileDimensions.World) | (string.IsNullOrWhiteSpace(profile.Style) ? 0 : ProfileDimensions.Style) |
        (string.IsNullOrWhiteSpace(profile.Methods) ? 0 : ProfileDimensions.Methods) | (string.IsNullOrWhiteSpace(profile.Rules) ? 0 : ProfileDimensions.Rules);
    public async Task<BookProject> AdoptAsync(BookProject book, TemplateChoice choice, ProfileDimensions dimensions)
    {
        var asset = await ReadAsync(choice.TemplateId).ConfigureAwait(false);
        return TemplateAdoptionRules.Adopt(book, asset, choice.VersionId, dimensions);
    }
}
