using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Constants;
namespace NovelGeneratePlugin.Standalone;
/// <summary>承载实际 Module 的开发对象图，不模拟 Host 的 Dock、授权或 ALC。</summary>
public sealed class PreviewRegistration : IPluginRegistration, IPluginIconRegistration, IWorkbenchCommandRegistration
{
    public PluginId PluginId => PluginIds.Plugin;
    public IServiceCollection Services { get; } = new ServiceCollection();
    public List<(DocumentDescriptor Descriptor, Type Model, Type View)> Documents { get; } = [];
    public List<(ToolDescriptor Descriptor, Type Model, Type View)> Tools { get; } = [];
    public List<Type> Lifecycles { get; } = [];
    public List<CommandDescriptor> Commands { get; } = [];
    public List<(CommandDescriptor Command, DocumentTypeId Target)> CommandTargets { get; } = [];
    public List<MenuCommandContributionDescriptor> Menus { get; } = [];
    public void UseLifecycle<T>() where T : class, IPluginLifecycle { Services.AddSingleton<T>(); Lifecycles.Add(typeof(T)); }
    public void AddDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPluginDocument where V : Control, new()
    { Documents.Add((descriptor, typeof(T), typeof(V))); Services.AddScoped<T>(); Services.AddTransient<V>(); }
    public void AddPersistableDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPersistablePluginDocument where V : Control, new()
        => throw new NotSupportedException("预览不实现 Host 信封保存。");
    public void AddTool<T, V>(ToolDescriptor descriptor) where T : class where V : Control, new()
    { Tools.Add((descriptor, typeof(T), typeof(V))); Services.AddSingleton<T>(); Services.AddTransient<V>(); }
    public string AddIcon(string localName, VectorIconDefinition definition) => $"plugin:{PluginId.Value}/{localName}";
    public void AddDocumentCommand(CommandDescriptor descriptor, DocumentTypeId targetDocumentTypeId) { Commands.Add(descriptor); CommandTargets.Add((descriptor, targetDocumentTypeId)); }
    public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) => Menus.Add(descriptor);
    public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) => throw new NotSupportedException("开发窗口不模拟全局快捷键。");
}
