using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed record StoryEntityItem(Guid Id, string Name) { public override string ToString() => Name; }
public sealed partial class MainDocument
{
    [ObservableProperty] private StoryEntityItem? _selectedStoryEntity;
    [ObservableProperty] private string _entityName = "";
    [ObservableProperty] private string _entityAliases = "";
    [ObservableProperty] private string _entityDescription = "";
    [ObservableProperty] private string _entityLockedField = "";
    [ObservableProperty] private string _entityLockedValue = "";
    [ObservableProperty] private StoryEntityKind _entityKind;
    [ObservableProperty] private string _contextQuery = "";
    [ObservableProperty] private bool _contextUseWorking = true;
    [ObservableProperty] private int _contextMaximumBytes = 100000;
    [ObservableProperty] private string _storyContextText = "登记人物及别名，或预览当前章会使用的前文与规范；此操作不请求模型。";
    [ObservableProperty] private string _storyContextStatus = "尚未装配上下文";
    private StoryContext? _storyContext;
    public ObservableCollection<StoryEntityItem> StoryEntities { get; } = [];
    public IReadOnlyList<StoryEntityKind> EntityKinds { get; } = Enum.GetValues<StoryEntityKind>();
    private bool HasEntityDraftChanges => _session is not null && _session.Current.Story.Editor !=
        (_session.Current.Story.Entities.FirstOrDefault(e => e.Id == _session.Current.Story.Editor.Id) is { } entity ? StoryEntityDraft.From(entity) : StoryEntityDraft.Empty);
    public bool CanSelectEntity => CanEdit && !HasEntityDraftChanges;
    private void NotifyStoryCommands()
    {
        OnPropertyChanged(nameof(CanSelectEntity)); NewStoryEntityCommand.NotifyCanExecuteChanged();
        SaveStoryEntityCommand.NotifyCanExecuteChanged(); DiscardEntityDraftCommand.NotifyCanExecuteChanged(); PreviewStoryContextCommand.NotifyCanExecuteChanged();
    }
    private StoryEntityDraft CaptureEntityDraft() => new(SelectedStoryEntity?.Id, EntityKind, EntityName, EntityAliases, EntityDescription, EntityLockedField, EntityLockedValue);
    partial void OnEntityNameChanged(string value) => CaptureEntityEditor();
    partial void OnEntityAliasesChanged(string value) => CaptureEntityEditor();
    partial void OnEntityDescriptionChanged(string value) => CaptureEntityEditor();
    partial void OnEntityLockedFieldChanged(string value) => CaptureEntityEditor();
    partial void OnEntityLockedValueChanged(string value) => CaptureEntityEditor();
    partial void OnEntityKindChanged(StoryEntityKind value) => CaptureEntityEditor();
    partial void OnContextQueryChanged(string value) => ClearStoryPreview();
    partial void OnContextUseWorkingChanged(bool value) => ClearStoryPreview();
    partial void OnContextMaximumBytesChanged(int value) => ClearStoryPreview();
    private void ClearStoryPreview() { _storyContext = null; StoryContextStatus = "预览条件已变化，请重新装配上下文。"; }
    private void CaptureEntityEditor()
    {
        if (_loading || _session is null || _closing.IsCancellationRequested) return;
        _session.Update(_session.Current with { Story = _session.Current.Story with { Editor = CaptureEntityDraft() } }); NotifyStoryCommands();
    }
    partial void OnSelectedStoryEntityChanged(StoryEntityItem? oldValue, StoryEntityItem? newValue)
    {
        if (_loading || _session is null) return;
        if (!CanSelectEntity)
        { _loading = true; try { SelectedStoryEntity = oldValue; } finally { _loading = false; } Notice = "请先保存实体或放弃表单草案。"; return; }
        var draft = newValue is null ? StoryEntityDraft.Empty : StoryEntityDraft.From(_session.Current.Story.Entities.Single(e => e.Id == newValue.Id));
        _session.Update(_session.Current with { Story = _session.Current.Story with { Editor = draft } }); LoadStory();
    }
    private void LoadStory()
    {
        if (_session is null) return;
        _loading = true;
        try
        {
            var story = _session.Current.Story; var draft = story.Editor;
            StoryEntities.Clear(); foreach (var entity in story.Entities) StoryEntities.Add(new(entity.Id, entity.Name));
            SelectedStoryEntity = StoryEntities.FirstOrDefault(e => e.Id == draft.Id);
            EntityName = draft.Name; EntityAliases = draft.Aliases; EntityDescription = draft.Description; EntityKind = draft.Kind;
            EntityLockedField = draft.LockedField; EntityLockedValue = draft.LockedValue;
        }
        finally { _loading = false; }
        NotifyStoryCommands(); UpdateStoryContextStatus();
    }
    [RelayCommand(CanExecute = nameof(CanSelectEntity))]
    private void NewStoryEntity()
    { _session!.Update(_session.Current with { Story = _session.Current.Story with { Editor = StoryEntityDraft.Empty } }); LoadStory(); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DiscardEntityDraft()
    {
        var story = _session!.Current.Story; var entity = story.Entities.FirstOrDefault(e => e.Id == story.Editor.Id);
        _session.Update(_session.Current with { Story = story with { Editor = entity is null ? StoryEntityDraft.Empty : StoryEntityDraft.From(entity) } }); LoadStory();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveStoryEntity() => RunAsync(async () =>
    {
        var story = (_session!.Current.Story with { Editor = CaptureEntityDraft() }).Commit();
        _session.Update(_session.Current with { Story = story }); var saved = await _session.SaveAsync(); Status = saved.Message; LoadStory();
        Notice = "实体登记已保存；改名保留原称作为别名。设定不等于已发生的剧情事实。";
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PreviewStoryContext() => RunAsync(async () =>
    {
        var book = _session!.Current; var chapter = SelectedChapter!.Id; var working = ContextUseWorking; var maximum = ContextMaximumBytes; var query = ContextQuery;
        var context = await Task.Run(() => new StoryContextBuilder().Build(book, chapter, working ? book.Revisions.ActiveRunId : null, working, maximum, query));
        if (SelectedChapter?.Id != chapter || ContextUseWorking != working || ContextMaximumBytes != maximum || ContextQuery != query ||
            context.Stamp != StoryMemory.Stamp(_session.Current, chapter, context.RunId, working)) throw new InvalidOperationException("装配期间内容已变化，请重新预览。");
        _storyContext = context; StoryContextText = context.Render() + (context.Omitted.Length == 0 ? "" : "\n\n预算内未携带：\n" + string.Join('\n', context.Omitted)); UpdateStoryContextStatus();
    });
    private void UpdateStoryContextStatus()
    {
        if (_storyContext is null || _session is null || SelectedChapter is null) return;
        var context = _storyContext; var book = _session.Current;
        StoryContextStatus = context.BookId == book.Id && context.ChapterId == SelectedChapter.Id &&
            (!context.UseWorking || context.RunId == book.Revisions.ActiveRunId) && context.Stamp == StoryMemory.Stamp(book, context.ChapterId, context.RunId, context.UseWorking)
            ? $"当前上下文：{context.Parts.Length} 项，{context.Utf8Bytes} 字节，省略 {context.Omitted.Length} 项；尚未发送模型。"
            : "旧上下文已过期：正文、前文、规则、设定或运行发生变化，请重新装配。";
    }
}
