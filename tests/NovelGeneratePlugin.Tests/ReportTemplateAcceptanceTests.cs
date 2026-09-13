using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class ReportTemplateAcceptanceTests
{
    [Fact]
    public async Task 删除外部原文后转换发布并分别采用到两书且重开保持独立快照()
    {
        await using var workspace = new TestWorkspace();
        var analysisModel = new NovelReportTests.Router();
        var context = await NovelReportTests.Setup(workspace, analysisModel);
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        var report = context.Reports.Read(context.Run.Id);
        var analysisRequests = analysisModel.Requests.Count;
        File.Delete(context.InputPath);

        var model = new ReportTemplateExecutionTests.Converter();
        var service = ReportTemplateExecutionTests.Service(workspace, model);
        var conversion = await service.PrepareAsync(context.Run.Id, ConnectionService.Bind(context.Run.Connection.Connection),
            "隔离验收模板", ProfileDimensions.World | ProfileDimensions.Style | ProfileDimensions.Methods, 8, 500000);
        await service.CreateAsync(conversion);
        await service.ExecuteAsync(conversion.Id, null, default);
        await new ReportTemplateDeliveryService(new ReportTemplateConversionStore(workspace.Paths), workspace.Templates).DeliverAsync(conversion.Id);
        var asset = await workspace.Templates.PublishAsync(await workspace.Templates.ReadAsync(conversion.TemplateId));
        var v1 = asset.Versions.Single();
        var choice = (await workspace.Templates.ChoicesAsync()).Single();
        var first = BookProject.Create("甲") with { Profile = new("甲原世界", "甲原文风", "甲原方法", "甲原规则") };
        var second = BookProject.Create("乙") with { Profile = new("乙原世界", "乙原文风", "乙原方法", "乙原规则") };
        first = await workspace.Templates.AdoptAsync(first, choice, ProfileDimensions.Style);
        second = await workspace.Templates.AdoptAsync(second, choice, ProfileDimensions.World | ProfileDimensions.Methods);
        workspace.Store.Create(workspace.ProjectPath("甲"), first);
        workspace.Store.Create(workspace.ProjectPath("乙"), second);

        // 已采用内容属于作品快照。修改甲书和发布模板 v2 均不能改变乙书或旧模板版本。
        workspace.Store.Save(workspace.ProjectPath("甲"), first with { Profile = first.Profile with { Style = "甲书独立调整" } }, 0);
        asset = await workspace.Templates.SaveDraftAsync(asset, asset.Draft with
        { Content = asset.Draft.Content with { World = "模板第二版世界", Style = "模板第二版文风" } });
        await workspace.Templates.PublishAsync(asset);

        var reopenedLibrary = new TemplateLibrary(new TemplateStore(workspace.Paths));
        var reopenedAsset = await reopenedLibrary.ReadAsync(asset.Id);
        var reopenedConversion = new ReportTemplateConversionStore(workspace.Paths).Read(conversion.Id);
        var reopenedReport = new NovelAnalysisReportService(new ReferenceSourceStore(workspace.Paths), new AnalysisRunStore(workspace.Paths)).Read(context.Run.Id);
        var reopenedFirst = new ProjectStore().Read(workspace.ProjectPath("甲")).Project;
        var reopenedSecond = new ProjectStore().Read(workspace.ProjectPath("乙")).Project;
        Assert.False(File.Exists(context.InputPath));
        Assert.Equal(report.Version, reopenedReport.Version);
        Assert.Equal(TemplateConversionState.DraftSaved, reopenedConversion.State);
        Assert.Equal(v1.Content, reopenedAsset.Versions[0].Content);
        Assert.Equal(v1.Provenance, reopenedAsset.Versions[0].Provenance);
        Assert.Equal(new WritingProfile("甲原世界", "甲书独立调整", "甲原方法", "甲原规则"), reopenedFirst.Profile);
        Assert.Equal(new WritingProfile(v1.Content.World, "乙原文风", v1.Content.Methods, "乙原规则"), reopenedSecond.Profile);
        Assert.Equal(v1.Id, reopenedFirst.AdoptedTemplate!.VersionId);
        Assert.Equal(v1.Id, reopenedSecond.AdoptedTemplate!.VersionId);
        await ReportTemplateExecutionTests.Service(workspace, model).ExecuteAsync(conversion.Id, null, default);
        await new ReportTemplateDeliveryService(new ReportTemplateConversionStore(workspace.Paths), reopenedLibrary).DeliverAsync(conversion.Id);
        Assert.Single(await reopenedLibrary.ListAsync());
        Assert.Single(model.Requests);
        Assert.Equal(analysisRequests, analysisModel.Requests.Count);
        Assert.Equal("模板第二版文风", (await reopenedLibrary.ReadAsync(asset.Id)).Draft.Content.Style);
    }
}
