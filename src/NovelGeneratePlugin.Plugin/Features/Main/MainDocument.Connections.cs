using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed partial class MainDocument
{
    public ObservableCollection<ModelConnection> ConnectionChoices { get; } = [];
    [ObservableProperty] private ModelConnection? _selectedBookConnection;
    [ObservableProperty] private string _bookConnectionStatus = "本书未绑定连接；仍可离线编辑";
    private async Task RefreshBookConnectionsAsync()
    {
        try
        {
            var catalog = await connections.ListAsync(); ConnectionChoices.Clear(); foreach (var connection in catalog.Connections) ConnectionChoices.Add(connection);
            var binding = _session?.Current.Connection;
            SelectedBookConnection = ConnectionChoices.SingleOrDefault(c => c.Id == binding?.ConnectionId);
            BookConnectionStatus = binding is null ? "本书未绑定连接；仍可离线编辑" : $"本书绑定：{binding.Name} · v{binding.Version}";
            if (binding is not null)
            {
                var state = await connections.StateAsync(binding);
                BookConnectionStatus += state switch
                {
                    CredentialState.ManagedByCodex => "；由 CLI 管理登录，尚未远程检测",
                    CredentialState.Missing => "；缺少密钥",
                    CredentialState.Unavailable => "；密钥不可用，请重新配置",
                    CredentialState.Session => "；会话密钥已配置",
                    _ => "；加密密钥已配置"
                };
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { BookConnectionStatus = "连接暂不可用，编辑不受影响：" + exception.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task RefreshConnections() => RunAsync(RefreshBookConnectionsAsync);
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task BindConnection() => RunAsync(async () =>
    {
        var selected = SelectedBookConnection ?? throw new InvalidOperationException("请先选择本机连接。");
        // 冻结用例先验证所选配置仍是当前版本；绑定失败不更新作品。
        var candidate = _session!.Current with { Connection = ConnectionService.Bind(selected) };
        await connections.FreezeAsync(candidate, ModelTask.Drafting);
        _session.Update(_session.Current with { Connection = candidate.Connection });
        var saved = await _session.SaveAsync(); Status = saved.Message; await RefreshBookConnectionsAsync();
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task UnbindConnection() => RunAsync(async () =>
    {
        _session!.Update(_session.Current with { Connection = null });
        var saved = await _session.SaveAsync(); Status = saved.Message; await RefreshBookConnectionsAsync();
    });
}
