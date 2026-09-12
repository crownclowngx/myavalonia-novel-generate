using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Infrastructure.Persistence;
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
        return services;
    }
}
