using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.TemplateLibrary;
namespace NovelGeneratePlugin.Features.Main;

public sealed partial class MainDocument
{
    public ObservableCollection<MaterialChoice> BookMaterials { get; } = [];
    [ObservableProperty] private MaterialChoice? _selectedBookMaterial;
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RefreshBookMaterials() => RunAsync(async () =>
    {
        var available = await materials.ListAsync(); BookMaterials.Clear();
        foreach (var material in available.Where(m => m.Analysis is not null && m.AnalysisStamp == m.SourceStamp)) BookMaterials.Add(new(material.Id, material.Name));
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task AdoptBookMaterial() => RunAsync(async () =>
    {
        if (SelectedBookMaterial is null) throw new InvalidOperationException("请选择材料工具中已保存的提炼结果。");
        var material = await materials.ReadAsync(SelectedBookMaterial.Id);
        // 只修改当前书的规范并追加来源，材料库和共享模板保持各自所有权。
        _session!.Update(MaterialRules.Adopt(_session.Current, material)); LoadProfile(); UpdatePlanningStatus(); UpdateStoryContextStatus();
        var saved = await _session.SaveAsync(); Status = saved.Message; Notice = "已采用到本书，原章纲可能需要重新保存；共享模板没有同步改变。";
    });
}
