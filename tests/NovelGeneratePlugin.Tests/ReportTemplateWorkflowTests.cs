using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Infrastructure.Export;
using NovelGeneratePlugin.Infrastructure.Import;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class ReportTemplateWorkflowTests
{
    internal static NovelAnalysisPanel AnalysisPanel(TestWorkspace workspace, NovelReportTests.Context context,
        NovelAnalysisActivity analysisActivity, NovelReportReader reader)
    {
        var sources = new ReferenceSourceStore(workspace.Paths);
        return new(new(new TxtSourceReader(), sources), sources, context.Runner, analysisActivity,
            new(sources, context.Store, workspace.Models, new NovelAnalysisNodePreparer()), workspace.Connections, reader, workspace.Interaction, workspace.Closing);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 模板已创建后登记失败再继续保留用户编辑且只产生一个模板(bool recoveryFails)
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var inner = new ReportTemplateConversionStore(workspace.Paths);
        var store = new ReportTemplateExecutionTests.FailSaveOnce(inner, r => r.State == TemplateConversionState.DraftSaved) { FailRecovery = recoveryFails };
        var model = new ReportTemplateExecutionTests.Converter(); var service = ReportTemplateExecutionTests.Service(workspace, model, store);
        await service.CreateAsync(run); await service.ExecuteAsync(run.Id, null, default);
        var delivery = new ReportTemplateDeliveryService(store, workspace.Templates);
        await Assert.ThrowsAsync<TemplateConversionSaveException>(() => delivery.DeliverAsync(run.Id));
        var asset = Assert.Single(await workspace.Templates.ListAsync());
        asset = await workspace.Templates.SaveDraftAsync(asset, asset.Draft with { Content = asset.Draft.Content with { Style = "作者已经修改的文风" } });
        await service.ExecuteAsync(run.Id, null, default); await delivery.DeliverAsync(run.Id);
        Assert.Single(await workspace.Templates.ListAsync()); Assert.Single(model.Requests);
        Assert.Equal("作者已经修改的文风", (await workspace.Templates.ReadAsync(asset.Id)).Draft.Content.Style);
        Assert.Equal(TemplateConversionState.DraftSaved, inner.Read(run.Id).State);
    }

    [Fact]
    public async Task 固定模板身份并发交付返回同一对象且来源冲突不覆盖()
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var store = new ReportTemplateConversionStore(workspace.Paths); var service = ReportTemplateExecutionTests.Service(workspace, new ReportTemplateExecutionTests.Converter());
        await service.CreateAsync(run); await service.ExecuteAsync(run.Id, null, default);
        await new ReportTemplateDeliveryService(store, workspace.Templates).DeliverAsync(run.Id);
        var draft = (await workspace.Templates.ReadAsync(run.TemplateId)).Draft; var id = Guid.NewGuid();
        var results = await Task.WhenAll(workspace.Templates.CreateGeneratedDraftAsync(id, draft), workspace.Templates.CreateGeneratedDraftAsync(id, draft));
        Assert.All(results, asset => Assert.Equal(id, asset.Id)); Assert.Equal(2, (await workspace.Templates.ListAsync()).Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.Templates.CreateGeneratedDraftAsync(id, draft with
        { Provenance = draft.Provenance! with { ConversionId = Guid.NewGuid() } }));
        Assert.Equal(draft.Provenance, (await workspace.Templates.ReadAsync(id)).Draft.Provenance);
    }

    [Fact]
    public async Task 同一转换重复启动共享任务且隐藏报告不取消模型()
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var converter = new ReportTemplateExecutionTests.Converter { Intercept = (_, _) => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return null; } };
        var service = ReportTemplateExecutionTests.Service(workspace, converter); await service.CreateAsync(run);
        await using var activity = new ReportTemplateActivity(service, new(new ReportTemplateConversionStore(workspace.Paths), workspace.Templates), workspace.Closing);
        await using var panel = new ReportTemplatePanel(service, activity, workspace.Connections, workspace.Closing); await panel.InitializeAsync();
        var task = activity.StartAsync(run.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Same(task, activity.StartAsync(run.Id));
            panel.SetReport(null); Assert.True(activity.IsActive(run.Id)); Assert.Empty(panel.History);
        }
        finally { release.Set(); }
        Assert.Equal(TemplateConversionState.DraftSaved, (await task).State);
        Assert.Single(converter.Requests); Assert.Single(await workspace.Templates.ListAsync());
    }

    [Fact]
    public async Task 生成草案不覆盖正在编辑的模板并可发布供两本书选择()
    {
        await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        var converter = new ReportTemplateExecutionTests.Converter(); var service = ReportTemplateExecutionTests.Service(workspace, converter);
        await using var activity = new ReportTemplateActivity(service, new(new ReportTemplateConversionStore(workspace.Paths), workspace.Templates), workspace.Closing);
        await using var conversion = new ReportTemplatePanel(service, activity, workspace.Connections, workspace.Closing);
        var reader = new NovelReportReader(context.Reports, new AnalysisReportWriter(), workspace.Interaction, conversion);
        await using var analysisActivity = new NovelAnalysisActivity(context.Runner, workspace.Closing);
        await using var analysis = AnalysisPanel(workspace, context, analysisActivity, reader);
        await using var tool = new TemplateLibraryTool(workspace.Templates, workspace.Closing, analysis: analysis);
        await tool.InitializeAsync(); tool.Name = "手工编辑未保存"; tool.World = "作者当前输入";
        await reader.LoadAsync(context.Run.Id); Assert.True(reader.CanConvert); reader.ConvertCommand.Execute(null); Assert.True(reader.ConversionExpanded);
        conversion.UseWorld = false; conversion.UseMethods = false; conversion.UseRules = false;
        await conversion.GenerateCommand.ExecuteAsync(null);
        Assert.Equal("作者当前输入", tool.World); Assert.True(tool.IsDirty);
        var generated = Assert.Single(await workspace.Templates.ListAsync()); Assert.Empty(generated.Versions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.OpenGeneratedDraftAsync(generated.Id));
        await conversion.OpenDraftCommand.ExecuteAsync(null);
        Assert.Contains("请先保存", conversion.Status);
        await tool.SaveDraftCommand.ExecuteAsync(null); await conversion.OpenDraftCommand.ExecuteAsync(null);
        Assert.Equal("已打开生成草案，可编辑后保存新版本。", conversion.Status);
        Assert.Equal(generated.Draft.Content.Style, tool.Style); Assert.Contains("报告版本", tool.SourceStatus);
        await using var first = workspace.CreateDocument(); await first.InitializeAsync(new MyAvaloniaManagement.PluginSdk.NewDocumentActivation("甲"), default);
        await tool.PublishVersionCommand.ExecuteAsync(null);
        await WaitForAsync(() => first.TemplateChoices.Count == 1);
        first.SelectedTemplateChoice = first.TemplateChoices[0];
        Assert.False(first.UseWorld); Assert.True(first.UseStyle); Assert.False(first.UseMethods); Assert.False(first.UseRules);
        var choice = Assert.Single(await workspace.Templates.ChoicesAsync());
        var a = await workspace.Templates.AdoptAsync(BookProject.Create("甲"), choice, ProfileDimensions.Style);
        var b = await workspace.Templates.AdoptAsync(BookProject.Create("乙"), choice, ProfileDimensions.Style);
        workspace.Store.Create(workspace.ProjectPath("甲"), a); workspace.Store.Create(workspace.ProjectPath("乙"), b);
        Assert.Equal(a.Profile.Style, workspace.Store.Read(workspace.ProjectPath("乙")).Project.Profile.Style);
        Assert.Single(converter.Requests);
    }

    [Fact]
    public void 长期规则只自动交付明确支持的条目()
    {
        var candidate = new TemplateConversionCandidate([new(ProfileDimensions.Rules,
            [new("不得无代价复活", "生命规则", TemplateRuleBasis.Supported, [1]),
             new("所有角色都应沉默", "", TemplateRuleBasis.Inferred, [2]), new("增加新组织", "", TemplateRuleBasis.Suggestion, [])], [])]);
        var profile = ReportTemplateDeliveryService.Profile(candidate);
        Assert.Contains("不得无代价复活", profile.Rules); Assert.DoesNotContain("所有角色", profile.Rules); Assert.DoesNotContain("增加", profile.Rules);
    }

    [Theory]
    [InlineData(false, 1d, 760)]
    [InlineData(true, 1.5d, 420)]
    public async Task 报告转换原生绑定在明暗主题和窄屏可操作(bool dark, double scale, int width)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch<bool>(async () =>
        {
            await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
            await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
            var converter = new ReportTemplateExecutionTests.Converter(); var service = ReportTemplateExecutionTests.Service(workspace, converter);
            await using var activity = new ReportTemplateActivity(service, new(new ReportTemplateConversionStore(workspace.Paths), workspace.Templates), workspace.Closing);
            await using var panel = new ReportTemplatePanel(service, activity, workspace.Connections, workspace.Closing);
            Guid? opened = null; panel.OpenDraftAsync = async id => { await workspace.Templates.ReadAsync(id); opened = id; };
            var reader = new NovelReportReader(context.Reports, new AnalysisReportWriter(), workspace.Interaction, panel);
            var view = new NovelReportView { DataContext = reader };
            var window = new Window { Width = width, Height = 1050, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            try
            {
                window.Show(); window.SetRenderScaling(scale); await reader.LoadAsync(context.Run.Id);
                reader.ConvertCommand.Execute(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(view.FindControl<Button>("ReportToTemplateButton")!.IsEnabled);
                var conversionView = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<ReportTemplateView>().Single();
                Assert.Equal(panel.Name, conversionView.FindControl<TextBox>("ConversionName")!.Text);
                await panel.GenerateCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Contains("模板草案已保存", conversionView.FindControl<TextBlock>("ConversionStatus")!.Text);
                Assert.NotEmpty(conversionView.FindControl<TextBox>("ConversionPreview")!.Text!); Assert.Single(await workspace.Templates.ListAsync());
                Assert.True(conversionView.FindControl<Button>("OpenGeneratedTemplate")!.IsEnabled);
                await panel.OpenDraftCommand.ExecuteAsync(null); Assert.Equal(panel.SelectedConversion!.Value.TemplateId, opened);
                var output = Environment.GetEnvironmentVariable("NOVEL_TEST_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, $"report-template-{(dark ? "dark-150" : "light-100")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                panel.SetReport(context.Reports.Read(context.Run.Id) with { IsComplete = false }); Assert.False(panel.CanGenerate);
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    [Fact]
    public async Task 重开失败转换时先显示已保存候选与未知费用()
    {
        await using var workspace = new TestWorkspace(); var context = await NovelReportTests.Setup(workspace, new NovelReportTests.Router());
        await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        var converter = new ReportTemplateExecutionTests.Converter { Intercept = (_, request) => ReportTemplateExecutionTests.Converter.Response(request) with { Usage = new(100, null) } };
        var service = ReportTemplateExecutionTests.Service(workspace, converter);
        await using var activity = new ReportTemplateActivity(service, new(new ReportTemplateConversionStore(workspace.Paths), workspace.Templates), workspace.Closing);
        await using (var first = new ReportTemplatePanel(service, activity, workspace.Connections, workspace.Closing))
        {
            await first.InitializeAsync(); first.SetReport(context.Reports.Read(context.Run.Id)); await first.GenerateCommand.ExecuteAsync(null);
            Assert.Equal(TemplateConversionState.NeedsAttention, first.SelectedConversion!.Value.State);
        }
        await using var reopened = new ReportTemplatePanel(service, activity, workspace.Connections, workspace.Closing);
        await reopened.InitializeAsync(); reopened.SetReport(context.Reports.Read(context.Run.Id)); await reopened.LoadHistoryAsync();
        Assert.Contains("未知 1 次", reopened.UsageSummary); Assert.Contains("原始候选", reopened.FailureCandidate);
        Assert.True(reopened.CanContinue); Assert.False(reopened.AcknowledgeCosts); Assert.Single(converter.Requests);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(20);
        Assert.True(predicate());
    }
}
