using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed record AnalysisTopicChoice(string Name, NovelReportDraft Draft) { public override string ToString() => Name; }
public sealed record AnalysisEvidenceChoice(int Fact, AnalysisEvidence Evidence)
{ public override string ToString() => $"F{Fact} · 位置 {Evidence.Range.Start} · {Evidence.Quote[..Math.Min(60, Evidence.Quote.Length)]}"; }

/// <summary>
/// 报告阅读状态与运行面板分开。只绑定当前专题和证据附近片段，不把整部来源放进 TextBox。
/// 切换运行会清空旧报告，异步加载通过代数核对，防止较慢的旧任务覆盖后来选择的报告。
/// </summary>
public sealed partial class NovelReportReader(NovelAnalysisReportService reports, IAnalysisReportWriter writer, IPluginWindowInteraction interaction, ReportTemplatePanel? conversion = null) : ObservableObject
{
    public ReportTemplatePanel? Conversion => conversion;
    public bool HasConversion => conversion is not null;
    public bool CanConvert => !IsBusy && _report?.IsComplete == true && conversion is not null;
    [ObservableProperty] private bool _conversionExpanded;
    private NovelAnalysisReport? _report;
    private long _generation;
    [ObservableProperty] private AnalysisTopicChoice? _selectedTopic;
    [ObservableProperty] private AnalysisEvidenceChoice? _selectedEvidence;
    [ObservableProperty] private string _summary = "选择运行并读取已保存报告。";
    [ObservableProperty] private string _topicText = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _sourceLocation = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _selectionStart;
    [ObservableProperty] private int _selectionEnd;
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<AnalysisTopicChoice> Topics { get; } = [];
    public ObservableCollection<AnalysisEvidenceChoice> Evidence { get; } = [];
    public bool CanRead => !IsBusy;
    public bool CanExport => !IsBusy && _report is not null;
    public bool CanLocate => CanExport && SelectedEvidence is not null;
    public void Clear()
    {
        _generation++; _report = null; conversion?.SetReport(null); Topics.Clear(); SelectedTopic = null; Evidence.Clear(); SelectedEvidence = null;
        TopicText = SourceText = SourceLocation = ""; Summary = "选择运行并读取已保存报告。"; Notify();
    }
    public async Task LoadAsync(Guid runId)
    {
        Clear(); var generation = _generation; IsBusy = true;
        try
        {
            var report = await Task.Run(() => reports.Read(runId)); if (generation != _generation) return;
            _report = report;
            Summary = $"{report.Name} · {(report.IsComplete ? "完整提供文件的报告候选" : "部分报告")}\n正文覆盖 {report.CoveredCharacters}/{report.SourceCharacters} UTF-16 字符；原著完整性未经确认。\n{report.QualityStatus}\n模型 {report.Model} · 报告版本 {report.Version[..12]}";
            if (report.Synthesis is not null) Topics.Add(new("综合结论", report.Synthesis));
            foreach (var part in report.Parts) Topics.Add(new(NovelReportRequests.Title(part.Dimension), part.Draft));
            SelectedTopic = Topics.FirstOrDefault(); Status = "报告已从本机保存结果读取，原 TXT 和模型均无需在线。";
            if (conversion is not null)
            {
                conversion.SetReport(report); await conversion.InitializeAsync();
                if (generation == _generation) await conversion.LoadHistoryAsync();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (generation == _generation) Status = error.Message; }
        finally { IsBusy = false; Notify(); }
    }
    partial void OnSelectedTopicChanged(AnalysisTopicChoice? value)
    {
        Evidence.Clear(); SelectedEvidence = null; SourceText = SourceLocation = "";
        if (value is null || _report is null) { TopicText = ""; return; }
        var text = new StringBuilder(value.Draft.Title + "\n\n");
        foreach (var claim in value.Draft.Claims)
        {
            text.AppendLine(claim.Heading + " · " + (claim.Certainty switch { AnalysisStatementKind.Explicit => "明确", AnalysisStatementKind.Inferred => "推断", _ => "不确定" })).AppendLine(claim.Text)
                .AppendLine("依据：" + string.Join("、", claim.Facts.Select(id => "F" + id))).AppendLine();
        }
        foreach (var question in value.Draft.OpenQuestions) text.AppendLine("待确认：" + question);
        TopicText = text.ToString();
        foreach (var id in value.Draft.Claims.SelectMany(c => c.Facts).Distinct())
            foreach (var evidence in _report.Integrated.Index.Findings.Single(f => f.Number == id).Value.Evidence.Distinct()) Evidence.Add(new(id, evidence));
        SelectedEvidence = Evidence.FirstOrDefault();
    }
    partial void OnSelectedEvidenceChanged(AnalysisEvidenceChoice? value) => Notify();
    partial void OnIsBusyChanged(bool value) => Notify();
    private void Notify()
    {
        OnPropertyChanged(nameof(CanRead)); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanLocate));
        OnPropertyChanged(nameof(CanConvert)); ConvertCommand.NotifyCanExecuteChanged();
        LocateCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanConvert))]
    private void Convert() => ConversionExpanded = true;
    [RelayCommand(CanExecute = nameof(CanLocate))]
    private async Task Locate()
    {
        var report = _report!; var evidence = SelectedEvidence!; var generation = _generation; IsBusy = true;
        try
        {
            var location = await Task.Run(() => reports.Locate(report.RunId, evidence.Evidence));
            if (generation != _generation) return;
            SourceText = location.Excerpt; SelectionStart = location.SelectionStart; SelectionEnd = location.SelectionStart + location.Length;
            SourceLocation = $"第 {location.Number} 段/章：{location.Section} · 原文位置 [{location.AbsoluteStart}, {location.AbsoluteStart + location.Length}) · 来源 {location.SourceHash[..12]}";
            Status = "证据已在保存的来源快照中定位并选中。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (generation == _generation) Status = error.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        var report = _report!; IsBusy = true;
        try
        {
            var path = await interaction.PickSaveFileAsync(new FilePickerSaveOptions
            {
                Title = "导出分析报告（请选择新文件名）",
                SuggestedFileName = "小说分析报告.md",
                DefaultExtension = "md",
                FileTypeChoices = [new FilePickerFileType("Markdown 报告") { Patterns = ["*.md"] }]
            });
            if (path is null) { Status = "已取消导出。"; return; }
            await writer.WriteAsync(report, path, default); Status = "已导出包含证据、覆盖及来源哈希的 Markdown 报告。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = error.Message; }
        finally { IsBusy = false; }
    }
}
