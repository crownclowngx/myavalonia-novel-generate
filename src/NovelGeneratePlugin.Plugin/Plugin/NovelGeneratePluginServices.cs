using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Infrastructure.Credentials;
namespace NovelGeneratePlugin.Plugin;

public static class NovelGeneratePluginServices
{
    public static IServiceCollection AddNovelGeneratePluginServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => WorkspacePaths.ForCurrentUser());
        services.AddSingleton<IProjectStore, ProjectStore>();
        services.AddSingleton<IProjectCatalog, CatalogStore>();
        services.AddSingleton<IRecoveryStore, RecoveryStore>();
        services.AddSingleton<IProjectLeaseProvider, FileProjectLeaseProvider>();
        services.AddSingleton<ProjectSessions>();
        services.AddSingleton<ITemplateStore, TemplateStore>();
        services.AddSingleton<TemplateLibrary>();
        services.AddSingleton<IConnectionStore, ConnectionStore>();
        services.AddSingleton<ICredentialVault, UserCredentialVault>();
        services.AddSingleton<ConnectionService>();
        return services;
    }
}
