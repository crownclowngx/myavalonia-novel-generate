using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Infrastructure.Credentials;
using NovelGeneratePlugin.Application.Export;
using NovelGeneratePlugin.Infrastructure.Export;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Infrastructure.Models;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Infrastructure.Import;
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
        services.AddSingleton<IReportTemplateConversionStore, ReportTemplateConversionStore>();
        services.AddSingleton<IConnectionStore, ConnectionStore>();
        services.AddSingleton<ICredentialVault, UserCredentialVault>();
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<IArtifactFiles, ArtifactFiles>();
        services.AddSingleton<ArtifactService>();
        services.AddSingleton(_ => DeepSeekTextModel.CreateClient());
        services.AddSingleton<DeepSeekTextModel>();
        services.AddSingleton<ICodexProcess, CodexProcess>();
        services.AddSingleton<CodexTextModel>();
        services.AddSingleton<ITextModel, TextModelRouter>();
        services.AddSingleton<IModelRequestStore, ModelRequestStore>();
        services.AddSingleton<ModelRequestService>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<IChapterWorkStore, ChapterWorkStore>();
        services.AddSingleton<ChapterGenerationService>();
        services.AddSingleton<IContinuousRunStore, ContinuousRunStore>();
        services.AddSingleton<ContinuousRunService>();
        services.AddSingleton<IMaterialStore, MaterialStore>();
        services.AddSingleton<IReferenceSourceStore, ReferenceSourceStore>();
        services.AddSingleton<ITextSourceReader, TxtSourceReader>();
        services.AddSingleton<NovelImportService>();
        services.AddSingleton<NovelChunkAnalysisService>();
        services.AddSingleton<IAnalysisRunStore, AnalysisRunStore>();
        services.AddSingleton<IAnalysisNodePreparer, NovelAnalysisNodePreparer>();
        services.AddSingleton<NovelAnalysisRunService>();
        services.AddSingleton<NovelAnalysisReportService>();
        services.AddSingleton<NovelAnalysisActivity>();
        services.AddSingleton<NovelAnalysisCandidateService>();
        services.AddSingleton<IAnalysisReportWriter, AnalysisReportWriter>();
        services.AddSingleton<NovelGeneratePlugin.Features.TemplateLibrary.NovelReportReader>();
        services.AddSingleton<NovelGeneratePlugin.Features.TemplateLibrary.NovelAnalysisPanel>();
        services.AddSingleton<MaterialCalibrationService>();
        services.AddSingleton<NovelGeneratePlugin.Features.TemplateLibrary.MaterialCalibrationPanel>();
        return services;
    }
}
