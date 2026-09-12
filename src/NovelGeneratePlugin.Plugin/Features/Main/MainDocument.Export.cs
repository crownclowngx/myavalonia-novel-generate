using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed partial class MainDocument
{
    private PreparedManuscript? _preparedExport;
    [ObservableProperty] private ManuscriptVersion _exportVersion;
    [ObservableProperty] private ManuscriptFormat _exportFormat;
    [ObservableProperty] private int _exportFirstChapter = 1;
    [ObservableProperty] private int _exportLastChapter = 1;
    [ObservableProperty] private bool _acknowledgeExportWarnings;
    [ObservableProperty] private string _exportReport = "先选择稿件版本与范围，预览检查后写入新文件。";
    public IReadOnlyList<ManuscriptVersion> ExportVersions { get; } = Enum.GetValues<ManuscriptVersion>();
    public IReadOnlyList<ManuscriptFormat> ExportFormats { get; } = Enum.GetValues<ManuscriptFormat>();
    private ExportSelection ExportSelection => new(ExportVersion, ExportFormat, ExportFirstChapter, ExportLastChapter);
    public bool CanWriteExport => CanEdit && _preparedExport is { } prepared && ReferenceEquals(prepared.Source, _session?.Current) && prepared.Selection == ExportSelection && (!prepared.HasWarnings || AcknowledgeExportWarnings);
    partial void OnExportVersionChanged(ManuscriptVersion value) => InvalidateExport();
    partial void OnExportFormatChanged(ManuscriptFormat value) => InvalidateExport();
    partial void OnExportFirstChapterChanged(int value) => InvalidateExport();
    partial void OnExportLastChapterChanged(int value) => InvalidateExport();
    partial void OnAcknowledgeExportWarningsChanged(bool value) => NotifyExportCommands();
    private void InvalidateExport() { _preparedExport = null; AcknowledgeExportWarnings = false; ExportReport = "选择已变化，请重新预览检查。"; NotifyExportCommands(); }
    private void NotifyExportCommands()
    {
        OnPropertyChanged(nameof(CanWriteExport)); PreviewExportCommand.NotifyCanExecuteChanged(); WriteExportCommand.NotifyCanExecuteChanged();
        BackupProjectCommand.NotifyCanExecuteChanged(); RestoreBackupCommand.NotifyCanExecuteChanged(); ExportTemplateFileCommand.NotifyCanExecuteChanged(); ImportTemplateFileCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PreviewExport() => RunAsync(async () =>
    {
        var snapshot = _session!.Current; var selection = ExportSelection;
        var prepared = await artifacts.PrepareAsync(snapshot, selection);
        if (!ReferenceEquals(snapshot, _session.Current) || selection != ExportSelection) throw new InvalidOperationException("预览期间作品或选择变化，请重试。");
        _preparedExport = prepared; AcknowledgeExportWarnings = false;
        var report = new StringBuilder($"选定 {prepared.Chapters.Length} 章，正文 {prepared.WordCount} 字（非空白 Unicode 字符）。\n仅进行了本地规则检查，未进行 AI 审校。\n");
        foreach (var chapter in prepared.Chapters)
        {
            var check = chapter.Check;
            report.AppendLine($"{chapter.Title}：命中 {check.Findings.Length}，冲突 {check.Conflicts.Length}，语义待检 {check.GuidanceCount}，{(check.Complete ? "本地检测完成" : "检测未完成")}。");
            foreach (var finding in check.Findings.Take(20)) report.AppendLine($"  {finding.Message}，位置 {finding.Start + 1}：{finding.Evidence[..Math.Min(finding.Evidence.Length, 120)]}");
        }
        report.AppendLine(prepared.HasWarnings ? "存在提示；阅读后可明确确认，按当前选定版本导出。" : "未发现本地确定性规则问题。点击写入新文件完成导出。");
        ExportReport = report.ToString(); NotifyExportCommands();
    });
    [RelayCommand(CanExecute = nameof(CanWriteExport))]
    private Task WriteExport() => RunAsync(async () =>
    {
        var prepared = _preparedExport ?? throw new InvalidOperationException("请先预览检查。");
        if (prepared.HasWarnings && !AcknowledgeExportWarnings) throw new InvalidOperationException("请先阅读并确认检查提示。");
        var extension = prepared.Selection.Format == ManuscriptFormat.Text ? "txt" : "md";
        var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions
        {
            Title = "导出选定稿件（请选择新文件名）",
            SuggestedFileName = "小说正文." + extension,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType("正文") { Patterns = ["*." + extension] }]
        }, _closing.Token);
        if (path is null) return;
        if (!ReferenceEquals(prepared.Source, _session!.Current) || prepared.Selection != ExportSelection) throw new InvalidOperationException("作品或导出选择已变化，请重新预览。");
        await artifacts.WriteAsync(prepared, path); Status = "已导出选定正文：" + path;
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task BackupProject() => RunAsync(async () =>
    {
        var saved = await _session!.SaveAsync(); if (!saved.Saved) throw new IOException("本书尚未可靠保存，未生成不完整备份。" + saved.Message);
        var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions { Title = "备份小说项目（请选择新文件名）", SuggestedFileName = "作品备份.noveldb", DefaultExtension = "noveldb", FileTypeChoices = [ProjectType] }, _closing.Token);
        if (path is null) return;
        await artifacts.BackupAsync(_session.Path, path); Status = "已创建一致性备份：" + path;
    });
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task RestoreBackup() => RunAsync(async () =>
    {
        var sources = await interaction.PickOpenFilesAsync(new FilePickerOpenOptions { Title = "选择要恢复的备份", AllowMultiple = false, FileTypeFilter = [ProjectType] }, _closing.Token);
        if (sources.Count == 0) return;
        var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions { Title = "恢复到新文件（保留作品身份）", SuggestedFileName = "恢复作品.noveldb", DefaultExtension = "noveldb", FileTypeChoices = [ProjectType] }, _closing.Token);
        if (path is null) return;
        await artifacts.BackupAsync(sources[0], path); Status = "已恢复到新文件：" + path;
        Notice = "恢复保留作品身份；打开前请关闭同一作品的其他工作区。当前作品未被替换。";
    });
    private static readonly FilePickerFileType TemplateFileType = new("小说模板文件") { Patterns = ["*.json"] };
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task ExportTemplateFile() => RunAsync(async () =>
    {
        var choice = SelectedTemplateChoice ?? throw new InvalidOperationException("请先选择模板。");
        var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions { Title = "导出此模板的草案与版本（请选择新文件）", SuggestedFileName = "创作模板.json", DefaultExtension = "json", FileTypeChoices = [TemplateFileType] }, _closing.Token);
        if (path is null) return;
        await artifacts.ExportTemplateAsync(choice.TemplateId, path); Status = "模板已导出，不含作品正文、运行状态或凭据。";
    });
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task ImportTemplateFile() => RunAsync(async () =>
    {
        var paths = await interaction.PickOpenFilesAsync(new FilePickerOpenOptions { Title = "导入为独立模板", AllowMultiple = false, FileTypeFilter = [TemplateFileType] }, _closing.Token);
        if (paths.Count == 0) return;
        var imported = await artifacts.ImportTemplateAsync(paths[0]); await RefreshTemplateChoicesAsync();
        SelectedTemplateChoice = TemplateChoices.FirstOrDefault(c => c.TemplateId == imported.Id); Status = "已导入独立模板：" + imported.Draft.Name;
    });
}
