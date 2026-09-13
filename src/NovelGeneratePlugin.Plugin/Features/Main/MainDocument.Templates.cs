using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed partial class MainDocument
{
    private bool _templateSubscribed, _refreshingTemplateChoices;
    private long _templateChoicesGeneration;
    private Task _templateRefresh = Task.CompletedTask;
    private (Guid BookId, Guid VersionId, ProfileDimensions Dimensions, WritingProfile Before)? _templatePreview;
    [ObservableProperty] private TemplateChoice? _selectedTemplateChoice;
    [ObservableProperty] private string _profileWorld = "";
    [ObservableProperty] private string _profileStyle = "";
    [ObservableProperty] private string _profileMethods = "";
    [ObservableProperty] private string _profileRules = "";
    [ObservableProperty] private bool _useWorld = true;
    [ObservableProperty] private bool _useStyle = true;
    [ObservableProperty] private bool _useMethods = true;
    [ObservableProperty] private bool _useRules = true;
    [ObservableProperty] private string _templateDifference = "先选择版本，再预览与本书规范的差异。";
    [ObservableProperty] private string _templateSource = "本书尚未采用模板";
    public ObservableCollection<TemplateChoice> TemplateChoices { get; } = [];
    private ProfileDimensions SelectedDimensions => (UseWorld ? ProfileDimensions.World : 0) | (UseStyle ? ProfileDimensions.Style : 0) |
        (UseMethods ? ProfileDimensions.Methods : 0) | (UseRules ? ProfileDimensions.Rules : 0);
    public bool CanApplyTemplate => CanEdit && _templatePreview is { } preview && _session is not null && SelectedTemplateChoice is not null &&
        preview.BookId == _session.Id && preview.VersionId == SelectedTemplateChoice.VersionId && preview.Dimensions == SelectedDimensions && preview.Before == _session.Current.Profile;
    partial void OnSelectedTemplateChoiceChanged(TemplateChoice? value)
    {
        if (value is not null && !_refreshingTemplateChoices)
        {
            // 空字段自动取消勾选，但不能重新启用作者已取消的维度，否则会覆盖原本要保留的本书规范。
            UseWorld = UseWorld && value.AvailableDimensions.HasFlag(ProfileDimensions.World); UseStyle = UseStyle && value.AvailableDimensions.HasFlag(ProfileDimensions.Style);
            UseMethods = UseMethods && value.AvailableDimensions.HasFlag(ProfileDimensions.Methods); UseRules = UseRules && value.AvailableDimensions.HasFlag(ProfileDimensions.Rules);
        }
        ClearTemplatePreview();
    }
    partial void OnUseWorldChanged(bool value) => ClearTemplatePreview();
    partial void OnUseStyleChanged(bool value) => ClearTemplatePreview();
    partial void OnUseMethodsChanged(bool value) => ClearTemplatePreview();
    partial void OnUseRulesChanged(bool value) => ClearTemplatePreview();
    partial void OnProfileWorldChanged(string value) => CaptureProfile();
    partial void OnProfileStyleChanged(string value) => CaptureProfile();
    partial void OnProfileMethodsChanged(string value) => CaptureProfile();
    partial void OnProfileRulesChanged(string value) => CaptureProfile();
    private void ClearTemplatePreview()
    {
        _templatePreview = null; TemplateDifference = "先选择版本，再预览与本书规范的差异。";
        ApplyTemplateCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanApplyTemplate));
    }
    private void CaptureProfile()
    {
        if (_loading || _session is null || _closing.IsCancellationRequested) return;
        _session.Update(_session.Current with { Profile = new WritingProfile(ProfileWorld, ProfileStyle, ProfileMethods, ProfileRules) });
        ClearTemplatePreview(); UpdateStoryContextStatus();
    }
    private void LoadProfile()
    {
        if (_session is null) return;
        _loading = true;
        try
        {
            var profile = _session.Current.Profile;
            ProfileWorld = profile.World; ProfileStyle = profile.Style; ProfileMethods = profile.Methods; ProfileRules = profile.Rules;
            TemplateSource = _session.Current.AdoptedTemplate is { } adopted ? $"来自 {adopted.Name} · v{adopted.VersionNumber}；本书修改独立保存" : "本书尚未采用模板";
        }
        finally { _loading = false; }
        ClearTemplatePreview();
    }
    private async Task RefreshTemplateChoicesAsync()
    {
        var generation = ++_templateChoicesGeneration; var choices = await templates.ChoicesAsync();
        if (_disposed || _closing.IsCancellationRequested || generation != _templateChoicesGeneration) return;
        var selected = SelectedTemplateChoice?.VersionId; _refreshingTemplateChoices = true;
        try
        {
            TemplateChoices.Clear(); foreach (var choice in choices) TemplateChoices.Add(choice);
            SelectedTemplateChoice = TemplateChoices.FirstOrDefault(c => c.VersionId == selected);
        }
        finally { _refreshingTemplateChoices = false; }
    }
    private void OnTemplateVersionsChanged()
    {
        void RefreshIfOpen() { if (!_disposed && !_closing.IsCancellationRequested) _templateRefresh = RefreshPublishedTemplatesAsync(); }
        if (_ui is null || ReferenceEquals(_ui, SynchronizationContext.Current)) RefreshIfOpen(); else _ui.Post(_ => RefreshIfOpen(), null);
    }
    private async Task RefreshPublishedTemplatesAsync()
    { try { await RefreshTemplateChoicesAsync(); } catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed) Status = "模板版本已更新，列表刷新失败，请点击刷新模板。"; } }
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task RefreshTemplates() => RunAsync(RefreshTemplateChoicesAsync);
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PreviewTemplate() => RunAsync(async () =>
    {
        if (SelectedTemplateChoice is not { } choice) throw new InvalidOperationException("请先选择已保存的模板版本。");
        var book = _session!.Current; var dimensions = SelectedDimensions;
        var adopted = await templates.AdoptAsync(book, choice, dimensions);
        var text = new StringBuilder($"准备采用：{choice}\n");
        if ((dimensions & ~choice.AvailableDimensions) != 0) text.AppendLine("已主动选择空模板字段：采用会清空对应本书内容，请核对下方差异。");
        foreach (var row in new[] { ("世界观", book.Profile.World, adopted.Profile.World), ("文风", book.Profile.Style, adopted.Profile.Style),
            ("方法", book.Profile.Methods, adopted.Profile.Methods), ("规则", book.Profile.Rules, adopted.Profile.Rules) })
        {
            if (row.Item2 == row.Item3) text.AppendLine(row.Item1 + "：保持当前内容");
            else text.AppendLine($"【{row.Item1}】\n当前：{row.Item2}\n采用后：{row.Item3}\n");
        }
        _templatePreview = (book.Id, choice.VersionId, dimensions, book.Profile); TemplateDifference = text.ToString();
        ApplyTemplateCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanApplyTemplate));
    });
    [RelayCommand(CanExecute = nameof(CanApplyTemplate))]
    private Task ApplyTemplate() => RunAsync(async () =>
    {
        var preview = _templatePreview ?? throw new InvalidOperationException("请先预览模板差异。");
        var before = _session!.Current;
        if (before.Id != preview.BookId || before.Profile != preview.Before) throw new InvalidOperationException("本书规范已经变化，请重新预览。");
        var adopted = await templates.AdoptAsync(before, SelectedTemplateChoice!, preview.Dimensions);
        var current = _session.Current;
        if (current.Profile != before.Profile) throw new InvalidOperationException("采用期间本书规范已变化，请重新预览。");
        // 只合入规范和来源，不能用等待前的旧快照覆盖作者在此期间的正文编辑。
        _session.Update(current with { Profile = adopted.Profile, AdoptedTemplate = adopted.AdoptedTemplate });
        LoadProfile(); var saved = await _session.SaveAsync(); Status = saved.Message;
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveAsTemplate() => RunAsync(async () =>
    {
        var book = _session!.Current;
        var name = (string.IsNullOrWhiteSpace(book.Title) ? "本书" : book.Title); name = name[..Math.Min(name.Length, 110)] + " 创作模板";
        var draft = new TemplateDraft(name, [], book.Profile, "来自本书规范：" + book.Title);
        var asset = await templates.CreatePublishedAsync(draft);
        await RefreshTemplateChoicesAsync(); SelectedTemplateChoice = TemplateChoices.FirstOrDefault(c => c.TemplateId == asset.Id);
        Notice = "已另存为独立模板 v1；正文、实时故事记忆和运行状态未写入模板。";
    });
}
