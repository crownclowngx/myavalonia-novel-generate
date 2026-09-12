using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed record ChapterItem(Guid Id, string Label) { public override string ToString() => Label; }

/// <summary>一本书一个 Document；窗口只通过公开文件选择端口接触宿主。</summary>
public sealed partial class MainDocument(ProjectSessions sessions, IProjectCatalog catalog, IRecoveryStore recovery, IPluginWindowInteraction interaction, NovelGeneratePlugin.Application.Templates.TemplateLibrary templates, NovelGeneratePlugin.Application.Connections.ConnectionService connections)
    : ObservableObject, IPluginDocument, IAsyncDisposable, IClosePreparation
{
    private ProjectSession? _session;
    private readonly CancellationTokenSource _closing = new();
    private SynchronizationContext? _ui;
    private Task _operation = Task.CompletedTask;
    private bool _disposed, _loading;
    private DocumentPresentationState _presentation = new("小说创作");
    private static readonly FilePickerFileType ProjectType = new("小说项目") { Patterns = ["*.noveldb"] };
    [ObservableProperty] private string _status = "新建项目，或打开已有 .noveldb 开始写作。";
    [ObservableProperty] private string _notice = "本地编辑可离线使用；保存不等于定稿。";
    [ObservableProperty] private string _bookTitle = "未命名小说";
    [ObservableProperty] private string _idea = "";
    [ObservableProperty] private string _projectPath = "尚未打开项目";
    [ObservableProperty] private bool _hasProject;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ChapterItem? _selectedChapter;
    [ObservableProperty] private RecentProject? _selectedRecent;
    [ObservableProperty] private RecoveryEntry? _selectedRecovery;
    [ObservableProperty] private string _chapterTitle = "";
    [ObservableProperty] private string _chapterOutline = "";
    [ObservableProperty] private string _chapterText = "";
    [ObservableProperty] private string _revisionStatus = "尚无工作稿或正式稿";
    [ObservableProperty] private string _revisionSummary = "";
    [ObservableProperty] private string _wordCount = "0 字";
    public ObservableCollection<ChapterItem> Chapters { get; } = [];
    public ObservableCollection<RecentProject> RecentProjects { get; } = [];
    public ObservableCollection<RecoveryEntry> Recoveries { get; } = [];
    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;
    public bool CanSwitch => !IsBusy && !_closing.IsCancellationRequested && !_disposed;
    public bool CanEdit => HasProject && CanSwitch;
    public async ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation); ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation) throw new NotSupportedException("请从小说工作区打开 .noveldb 项目。");
        _ui = SynchronizationContext.Current;
        _presentation = new DocumentPresentationState(string.IsNullOrWhiteSpace(activation.Title) ? "小说创作" : activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        await RefreshListsAsync();
    }
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnHasProjectChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        NotifyRuleDraft(); DiscardRuleDraftCommand.NotifyCanExecuteChanged(); SaveRuleVersionCommand.NotifyCanExecuteChanged(); CheckLocalRulesCommand.NotifyCanExecuteChanged(); LocateRuleFindingCommand.NotifyCanExecuteChanged();
        RefreshConnectionsCommand.NotifyCanExecuteChanged(); BindConnectionCommand.NotifyCanExecuteChanged(); UnbindConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSwitch));
        CommitDraftCommand.NotifyCanExecuteChanged(); FinalizeChapterCommand.NotifyCanExecuteChanged();
        DiscardWorkingCommand.NotifyCanExecuteChanged(); RollbackFormalCommand.NotifyCanExecuteChanged();
        RefreshTemplatesCommand.NotifyCanExecuteChanged(); PreviewTemplateCommand.NotifyCanExecuteChanged();
        ApplyTemplateCommand.NotifyCanExecuteChanged(); SaveAsTemplateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplyTemplate));
        NewProjectCommand.NotifyCanExecuteChanged(); NewFromTemplateCommand.NotifyCanExecuteChanged(); OpenProjectCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
        AddChapterCommand.NotifyCanExecuteChanged(); AddVolumeCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged(); RestoreRecoveryCommand.NotifyCanExecuteChanged(); RefreshLibraryCommand.NotifyCanExecuteChanged();
    }
    partial void OnBookTitleChanged(string value) => CaptureEdit();
    partial void OnIdeaChanged(string value) => CaptureEdit();
    partial void OnChapterTitleChanged(string value) => CaptureEdit();
    partial void OnChapterOutlineChanged(string value) => CaptureEdit();
    partial void OnRevisionSummaryChanged(string value) => CaptureEdit();
    partial void OnChapterTextChanged(string value)
    { WordCount = value.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r)) + " 字（非空白字符）"; CaptureEdit(); }
    partial void OnSelectedChapterChanged(ChapterItem? value)
    {
        if (_loading || value is null || _session is null) return;
        LoadChapter(value.Id);
    }
    private void LoadChapter(Guid id)
    {
        var chapter = _session!.Current.Chapters.Single(c => c.Id == id);
        _loading = true;
        try { ChapterTitle = chapter.Title; ChapterOutline = chapter.Outline; ChapterText = chapter.Text; RevisionSummary = chapter.Summary; }
        finally { _loading = false; }
        UpdateRevisionStatus();
    }
    private void CaptureEdit()
    {
        if (_loading || _session is null || _closing.IsCancellationRequested) return;
        var project = _session.Current;
        if (SelectedChapter is not null)
        {
            var index = project.Chapters.IndexOf(project.Chapters.Single(c => c.Id == SelectedChapter.Id));
            if (index >= 0)
            {
                var chapter = project.Chapters[index];
                project = project with { Chapters = project.Chapters.SetItem(index, chapter with { Title = ChapterTitle, Outline = ChapterOutline, Text = ChapterText, Summary = RevisionSummary }) };
                var label = ChapterLabel(project, project.Chapters[index]);
                var itemIndex = Chapters.IndexOf(SelectedChapter);
                if (itemIndex >= 0 && SelectedChapter.Label != label)
                {
                    _loading = true;
                    try { var item = new ChapterItem(chapter.Id, label); Chapters[itemIndex] = item; SelectedChapter = item; }
                    finally { _loading = false; }
                }
            }
        }
        _session.Update(project with { Title = BookTitle, Idea = Idea });
        UpdatePresentation(); UpdateRevisionStatus();
    }
    private static string ChapterLabel(BookProject project, Chapter chapter)
        => project.Volumes.Single(v => v.Id == chapter.VolumeId).Title + " / " + (string.IsNullOrWhiteSpace(chapter.Title) ? "未命名章节" : chapter.Title);
    private void UpdatePresentation()
    {
        _presentation = new DocumentPresentationState((string.IsNullOrWhiteSpace(BookTitle) ? "未命名小说" : BookTitle) + (_session?.Status.State == SaveState.Saved ? "" : " *"));
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void OnSessionStateChanged(object? sender, EventArgs args)
    {
        void Apply()
        {
            if (_disposed || !ReferenceEquals(sender, _session)) return;
            Status = _session!.Status.Message; UpdatePresentation(); UpdateRevisionStatus();
        }
        if (_ui is not null && SynchronizationContext.Current != _ui) _ui.Post(_ => Apply(), null); else Apply();
    }
    private void UpdateRevisionStatus()
    {
        UpdateLocalRuleStatus();
        if (_session is null || SelectedChapter is null) return;
        var ledger = _session.Current.Revisions; var head = ledger.Head(SelectedChapter.Id);
        var working = ledger.Get(head.WorkingId); var formal = ledger.Get(head.FormalId);
        var check = working?.Check switch { RevisionCheck.Passed => "检查通过", RevisionCheck.Failed => "检查失败", _ => "未检查" };
        RevisionStatus = $"工作稿：{(working is null ? "无" : working.Id.ToString("N")[..8] + " / " + check)}  ·  正式稿：{(formal is null ? "未定稿" : formal.Id.ToString("N")[..8])}";
        if (working is not null && working.TextHash != RevisionRules.Hash(ChapterText)) RevisionStatus += "  ·  编辑稿有未提交修改";
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task CommitDraft() => RunAsync(async () =>
    {
        var project = _session!.Current; var chapter = project.Chapters.Single(c => c.Id == SelectedChapter!.Id);
        var head = project.Revisions.Head(chapter.Id);
        var submission = new DraftSubmission(project.Id, chapter.Id, head.WorkingId, head.FormalId, RevisionRules.Hash(chapter.Text),
            RevisionRules.ContextStamp(project, chapter.Id), chapter.Text, RevisionSummary, [], RevisionCheck.NotChecked,
            project.Revisions.ActiveRunId ?? Guid.NewGuid(), Guid.NewGuid());
        await _session.CommitRevisionChangeAsync(current => RevisionRules.CommitWorking(current, submission), _closing.Token);
        UpdateRevisionStatus();
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task FinalizeChapter() => RunAsync(async () =>
    {
        var id = SelectedChapter!.Id; var head = _session!.Current.Revisions.Head(id);
        if (head.WorkingId is not Guid working) throw new InvalidOperationException("请先提交本章工作稿。");
        await _session.CommitRevisionChangeAsync(current => RevisionRules.FinalizeChapter(current, id, working, authorConfirmed: true), _closing.Token);
        UpdateRevisionStatus();
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task DiscardWorking() => RunAsync(async () =>
    {
        await _session!.CommitRevisionChangeAsync(RevisionRules.DiscardWorking, _closing.Token);
        RevisionSummary = ""; UpdateRevisionStatus(); Notice = "本轮工作稿已放弃；编辑缓冲、正式稿与历史仍保留。";
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RollbackFormal() => RunAsync(async () =>
    {
        var id = SelectedChapter!.Id;
        await _session!.CommitRevisionChangeAsync(current => RevisionRules.RollbackFrom(current, id), _closing.Token);
        RevisionSummary = ""; UpdateRevisionStatus(); Notice = "本章及后续正式指针与本轮工作稿已撤销；正文历史和编辑缓冲保留。";
    });
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task NewProject() => RunAsync(() => CreateProjectAsync(false));
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task NewFromTemplate() => RunAsync(() => CreateProjectAsync(true));
    private async Task CreateProjectAsync(bool useTemplate)
    {
        var project = BookProject.Create(string.IsNullOrWhiteSpace(BookTitle) ? "未命名小说" : BookTitle, Idea);
        // 默认项只在创建时建议；目录不可用不阻止作者离线创建和编辑作品。
        try { project = project with { Connection = await connections.DefaultForNewBookAsync() }; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Notice = "默认连接未读取；本书仍可离线编辑。"; }
        // 模板初始化在创建文件前完成；失败不会留下一本缺少所选规范的半成品作品。
        if (useTemplate)
        {
            var choice = SelectedTemplateChoice ?? throw new InvalidOperationException("请先选择一个已保存的模板版本。");
            project = await templates.AdoptAsync(project, choice, SelectedDimensions);
        }
        var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions { Title = "创建小说项目（请选择新文件名）", SuggestedFileName = "新作品.noveldb", DefaultExtension = "noveldb", FileTypeChoices = [ProjectType] }, _closing.Token);
        if (path is null) return;
        await SwitchAsync(() => sessions.CreateAsync(path, project, _closing.Token));
    }
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task OpenProject() => RunAsync(async () =>
    {
        var files = await interaction.PickOpenFilesAsync(new FilePickerOpenOptions { Title = "打开小说项目", AllowMultiple = false, FileTypeFilter = [ProjectType] }, _closing.Token);
        if (files.Count > 0) await SwitchAsync(() => sessions.OpenAsync(files[0], _closing.Token));
    });
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task OpenRecent() => RunAsync(async () =>
    { if (SelectedRecent is not null) await SwitchAsync(() => sessions.OpenAsync(SelectedRecent.Path, _closing.Token)); });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Save() => RunAsync(async () =>
    {
        var result = await _session!.SaveAsync(); Status = result.Message;
        if (result.Saved)
            try { await Task.Run(() => catalog.Record(new RecentProject(_session.Id, BookTitle, ProjectPath, DateTimeOffset.UtcNow))); }
            catch (Exception exception) { Notice = "正文已保存，最近项目索引未更新：" + exception.Message; }
        await RefreshListsAsync();
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddChapter() => ApplyBookEdit(() => BookEdits.AddChapter(_session!.Current, SelectedChapter?.Id));
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddVolume() => ApplyBookEdit(() => BookEdits.AddVolume(_session!.Current));
    private void ApplyBookEdit(Func<BookEditResult> edit)
    {
        if (!CanEdit) return;
        try { var result = edit(); _session!.Update(result.Project); ReloadChapters(result.SelectedChapterId); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { Status = exception.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task RefreshLibrary() => RunAsync(RefreshListsAsync);
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task RestoreRecovery() => RunAsync(async () =>
    {
        if (SelectedRecovery is null) return;
        var snapshot = await Task.Run(() => recovery.Read(SelectedRecovery.Path));
        var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions { Title = "恢复为独立的新项目（原项目不覆盖）", SuggestedFileName = "恢复作品.noveldb", DefaultExtension = "noveldb", FileTypeChoices = [ProjectType] }, _closing.Token);
        if (path is null) return;
        await SwitchAsync(() => sessions.CreateAsync(path, snapshot.Project.CopyAsNew(), _closing.Token));
        Notice = "恢复副本已另存为独立作品；原项目和原恢复文件均保留。";
    });
    private async Task SwitchAsync(Func<Task<ProjectSession>> acquire)
    {
        if (_session is not null)
        {
            var save = await _session.SaveAsync();
            if (!save.Saved) throw new IOException("当前项目尚未保存，请先重试保存。" + save.Message);
        }
        var next = await acquire();
        try
        {
            if (_session is not null) { _session.StateChanged -= OnSessionStateChanged; await _session.DisposeAsync(); }
        }
        catch { await next.DisposeAsync(); throw; }
        _session = next; _session.StateChanged += OnSessionStateChanged;
        _loading = true;
        try { BookTitle = next.Current.Title; Idea = next.Current.Idea; ProjectPath = next.Path; HasProject = true; }
        finally { _loading = false; }
        LoadProfile();
        LoadRules();
        ReloadChapters(next.Current.Chapters[0].Id);
        Status = next.Status.Message; Notice = next.CatalogWarning ?? "本地编辑可离线使用；保存不等于定稿。"; UpdatePresentation();
        await RefreshListsAsync();
    }
    private void ReloadChapters(Guid selection)
    {
        _loading = true;
        try
        {
            Chapters.Clear();
            foreach (var chapter in _session!.Current.Chapters) Chapters.Add(new ChapterItem(chapter.Id, ChapterLabel(_session.Current, chapter)));
            SelectedChapter = Chapters.Single(c => c.Id == selection);
        }
        finally { _loading = false; }
        // 不能依赖 SelectedChapter 变化回调；跨书的选择记录可能相等，但正文归属已经改变。
        LoadChapter(selection);
    }
    private async Task RefreshListsAsync()
    {
        await RefreshBookConnectionsAsync();
        try { await RefreshTemplateChoicesAsync(); }
        catch (Exception exception) { Notice = "模板列表不可用，本书仍可编辑：" + exception.Message; }
        try
        {
            var recent = await Task.Run(catalog.List); RecentProjects.Clear(); foreach (var item in recent) RecentProjects.Add(item);
        }
        catch (Exception exception) { Notice = "最近项目列表不可用，可直接打开作品：" + exception.Message; }
        try
        {
            var entries = await Task.Run(recovery.List); Recoveries.Clear(); foreach (var item in entries) Recoveries.Add(item);
        }
        catch (Exception exception) { Notice = "恢复列表读取失败：" + exception.Message; }
    }
    private Task RunAsync(Func<Task> operation)
    {
        if (!CanSwitch) return Task.CompletedTask;
        return _operation = RunCoreAsync(operation);
    }
    private async Task RunCoreAsync(Func<Task> operation)
    {
        IsBusy = true;
        try { await operation(); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Status = exception.Message; }
        finally { IsBusy = false; }
    }
    /// <summary>
    /// 开发窗口在释放 DI Scope 之前检查落盘结果。若两种存储都失败，Scope 尚未释放，作者仍可重试。
    /// 真实 Host 没有相同的关闭否决接口，因此 DisposeAsync 仍独立承担恢复落盘职责。
    /// </summary>
    public async Task<bool> SaveBeforeCloseAsync()
    {
        await _operation;
        if (_session is null) return true;
        var result = await _session.PrepareCloseAsync();
        Status = result.Message;
        return result.Saved || result.RecoveryAvailable;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _closing.Cancel(); NotifyCommands(); await _operation;
        if (_session is not null) { await _session.DisposeAsync(); _session.StateChanged -= OnSessionStateChanged; }
        _disposed = true; _closing.Dispose();
    }
}
