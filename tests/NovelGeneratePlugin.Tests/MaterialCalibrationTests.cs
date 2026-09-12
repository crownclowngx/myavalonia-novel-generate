using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class MaterialCalibrationTests
{
    internal const string SourceText = "写作应先建立期待，再制造阻碍，最后兑现。使用短句，以具体动作表达情绪，避免抽象评价。";
    internal static MaterialAnalysis Analysis(MaterialPurpose purpose) => purpose == MaterialPurpose.Methods ?
        new([new("期待与兑现", "读者尚不知道结果时", ["建立具体期待", "制造可见阻碍", "兑现先前承诺"], "空喊紧张却没有事件", "避免强行拖延", [new(1, "先建立期待，再制造阻碍，最后兑现", false)])], []) :
        new([], [new("动作表达情绪", "用短句和动作替代抽象评价", "他攥紧信封。", "他非常紧张。", [new(1, "使用短句，以具体动作表达情绪", false)])]);
    internal static MaterialTrial Trial(MaterialDocument material) => new("查明门前钥匙来源", "邮差发现钥匙", "门锁已损坏", "发现门框的新划痕", string.IsNullOrWhiteSpace(material.Methods) ? "" : "建立具体期待",
        "邮差独自站在门前，脚边躺着一把钥匙。", "门前只有邮差一人。他停步，低头看见钥匙。", [new(material.Constraints, "邮差独自站在门前", "门前只有邮差一人")]);
    internal static async Task<MaterialDocument> MaterialAsync(TestWorkspace workspace, MaterialPurpose purpose = MaterialPurpose.Methods)
    {
        var preset = new ModelPreset("gpt-6-astra", 8192, "low"); var connection = await workspace.Connections.SaveAsync(null, new("材料测试", ModelProvider.CodexCli, "", @"C:\unit\codex.exe", preset, preset, preset));
        return MaterialDocument.Create() with { Name = "短材料", Purpose = purpose, Text = SourceText, Constraints = "只有邮差一人出场", Connection = ConnectionService.Bind(connection) };
    }
    private static MaterialCalibrationService Service(TestWorkspace workspace, ITextModel model) => new(new MaterialStore(workspace.Paths), workspace.Connections, new(model, new ModelRequestStore(workspace.Paths)));
    [Theory]
    [InlineData(MaterialPurpose.Methods)]
    [InlineData(MaterialPurpose.Style)]
    public async Task 方法和文风材料完成提炼试写批注及本书模板独立采用(MaterialPurpose purpose)
    {
        await using var workspace = new TestWorkspace(); var material = await MaterialAsync(workspace, purpose);
        var fake = new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(Analysis(purpose))), _ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(Trial(material with { Methods = purpose == MaterialPurpose.Methods ? "建立具体期待" : "" }))));
        var service = Service(workspace, fake); material = await service.AnalyzeAsync(material, default); material = await service.TrialAsync(material, default);
        material = await service.SaveAsync(material with { Feedback = "采用动作表达，减少解释", ChosenSample = "B" }); Assert.NotNull(material.Trial); Assert.Equal(2, material.Budgets.Length); Assert.Equal(2, service.Usage(material).Count);
        Assert.Equal(2, service.Usage(material).Select(r => r.BudgetId).Distinct().Count()); Assert.All(fake.Requests, r => Assert.Equal(material.Id, r.Configuration.BookId));
        var book = MaterialRules.Adopt(BookProject.Create("本书"), material); Assert.Single(book.MaterialSources); Assert.Equal("B", book.MaterialSources[0].ChosenSample);
        Assert.Empty(await workspace.Templates.ListAsync()); var adoption = MaterialRules.Adoption(material);
        var template = await workspace.Templates.CreatePublishedAsync(new(material.Name, [], new("", adoption.Style, adoption.Methods, ""), adoption.Source)); Assert.Single(template.Versions);
        workspace.Store.Create(workspace.ProjectPath(), book); var reopened = workspace.Store.Read(workspace.ProjectPath()).Project; Assert.Equal(book.Profile, reopened.Profile); Assert.Single(reopened.MaterialSources);
        material = await service.SaveAsync(material with { Methods = "后来改动的共享方法" }); Assert.NotEqual(material.Methods, reopened.Profile.Methods); Assert.Equal(adoption.Methods, template.Versions[0].Content.Methods);
    }
    [Fact]
    public void 未处理片段与伪造证据不能计入已分析内容()
    {
        var material = MaterialDocument.Create() with { Text = SourceText + new string('后', 1000), AnalyzeCharacters = 10 }; Assert.Equal(10, material.Segments().Sum(s => s.Length));
        Assert.Throws<InvalidDataException>(() => MaterialRules.ValidateAnalysis(material, Analysis(MaterialPurpose.Methods)));
        material = material with { AnalyzeCharacters = 12000 }; var analysis = Analysis(MaterialPurpose.Methods);
        analysis = analysis with { Methods = analysis.Methods.SetItem(0, analysis.Methods[0] with { Evidence = [new(20, "不存在的来源", true)] }) };
        Assert.Throws<InvalidDataException>(() => MaterialRules.ValidateAnalysis(material, analysis));
    }
    [Fact]
    public async Task 材料来源变化后不能采用旧提炼且并发版本冲突不覆盖()
    {
        await using var workspace = new TestWorkspace(); var material = await MaterialAsync(workspace); var service = Service(workspace, new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(Analysis(MaterialPurpose.Methods)))));
        material = await service.AnalyzeAsync(material, default); var newer = await service.SaveAsync(material with { Text = "新的材料原文" });
        Assert.Throws<InvalidOperationException>(() => MaterialRules.Adoption(newer)); await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(material with { Name = "旧版本" }));
        Assert.Equal("新的材料原文", (await service.ReadAsync(material.Id)).Text);
    }
    [Fact]
    public void 试写方法与事实约束必须有当前文本证据()
    {
        var material = MaterialDocument.Create() with { Methods = "建立具体期待", Constraints = "只有邮差一人出场" }; var trial = Trial(material); MaterialRules.ValidateTrial(material, trial);
        Assert.Throws<InvalidDataException>(() => MaterialRules.ValidateTrial(material, trial with { MethodQuote = "伪造方法" }));
        Assert.Throws<InvalidDataException>(() => MaterialRules.ValidateTrial(material, trial with { Evidence = [new(material.Constraints, "另一个人", "门前只有邮差一人")] }));
    }
    [Fact]
    public async Task 短文字导入支持中英文且拒绝超限或其他格式()
    {
        await using var workspace = new TestWorkspace(); var service = Service(workspace, new ScriptedTextModel()); var path = Path.Combine(workspace.Root, "课件.md"); await File.WriteAllTextAsync(path, SourceText + "\nhttps://example.com/ 不执行此链接。");
        Assert.Contains(SourceText, await service.ReadTextFileAsync(path)); await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadTextFileAsync(Path.Combine(workspace.Root, "演示.pdf")));
        await File.WriteAllTextAsync(path, new string('字', 20001)); await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadTextFileAsync(path));
    }
    [Fact]
    public async Task 模型失败保留材料和独立请求预算记录()
    {
        await using var workspace = new TestWorkspace(); var material = await MaterialAsync(workspace); var service = Service(workspace, new ScriptedTextModel(_ => throw new IOException("注入断流")));
        await Assert.ThrowsAsync<MaterialTaskException>(() => service.AnalyzeAsync(material, default)); var saved = await service.ReadAsync(material.Id);
        Assert.Equal(SourceText, saved.Text); Assert.Single(saved.Budgets); Assert.Null(saved.Analysis); Assert.Single(service.Usage(saved));
    }
    [Fact]
    public async Task 共享工具分析不会被关闭一本作品取消且工具关闭会取消保存()
    {
        await using var workspace = new TestWorkspace(); var material = await MaterialAsync(workspace); var delayed = new WaitingModel(); var service = Service(workspace, delayed);
        await using var panel = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing);
        await panel.InitializeAsync(); panel.Text = SourceText; panel.SelectedConnection = Assert.Single(panel.Connections);
        await using var document = workspace.CreateDocument(); await document.InitializeAsync(new NewDocumentActivation("独立所有者"), default);
        var operation = panel.AnalyzeMaterialCommand.ExecuteAsync(null); await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); await document.DisposeAsync(); Assert.False(delayed.Cancelled);
        Assert.True(await panel.SaveBeforeCloseAsync()); await operation; Assert.True(delayed.Cancelled); Assert.False(panel.IsBusy);
        var saved = Assert.Single(await service.ListAsync()); Assert.Equal(SourceText, saved.Text); Assert.Single(saved.Budgets);
    }
    [Fact]
    public async Task 材料面板关闭保存未提炼的作者草案()
    {
        await using var workspace = new TestWorkspace(); var service = Service(workspace, new ScriptedTextModel()); var panel = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing);
        await panel.InitializeAsync(); panel.Text = "还没有分析的草案"; panel.Feedback = "作者记下的问题"; await panel.DisposeAsync();
        var saved = Assert.Single(await service.ListAsync()); Assert.Equal(panel.Text, saved.Text); Assert.Equal(panel.Feedback, saved.Feedback); Assert.Null(saved.Analysis);
    }
    [Fact]
    public async Task 第七版作品备份升级且不伪造材料采用记录()
    {
        await using var workspace = new TestWorkspace(); var book = BookProject.Create("旧书"); workspace.Store.Create(workspace.ProjectPath(), book);
        using (var connection = ProjectStore.Connect(workspace.ProjectPath())) { using var command = connection.CreateCommand(); command.CommandText = "UPDATE project SET snapshot=json_remove(snapshot,'$.MaterialSources'); PRAGMA user_version=7;"; command.ExecuteNonQuery(); }
        var read = workspace.Store.Read(workspace.ProjectPath()); Assert.Empty(read.Project.MaterialSources); Assert.Single(Directory.GetFiles(workspace.Root, "*.before-v" + ProjectStore.SchemaVersion + "-*.noveldb"));
    }
    private sealed class WaitingModel : ITextModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Cancelled;
        public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken ct)
        { Started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Cancelled = true; throw; } throw new InvalidOperationException(); }
    }
    [Fact]
    public async Task 并发保存失败不能自动套用新版本覆盖另一方修改()
    {
        await using var workspace = new TestWorkspace(); var material = await MaterialAsync(workspace); var service = Service(workspace, new ScriptedTextModel()); material = await service.SaveAsync(material);
        await using var a = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing);
        await using var b = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing);
        await a.InitializeAsync(); await b.InitializeAsync(); a.SelectedMaterial = Assert.Single(a.Materials); b.SelectedMaterial = Assert.Single(b.Materials);
        await a.ReadMaterialCommand.ExecuteAsync(null); await b.ReadMaterialCommand.ExecuteAsync(null);
        a.Feedback = "A 的作者修改"; await a.SaveMaterialCommand.ExecuteAsync(null); b.Style = "B 的旧快照编辑";
        await b.SaveMaterialCommand.ExecuteAsync(null); await b.SaveMaterialCommand.ExecuteAsync(null);
        Assert.True(b.IsDirty); Assert.Equal("A 的作者修改", (await service.ReadAsync(material.Id)).Feedback); Assert.NotEqual(b.Style, (await service.ReadAsync(material.Id)).Style);
        await b.DiscardMaterialEditsCommand.ExecuteAsync(null); Assert.False(b.IsDirty); Assert.Equal(a.Feedback, b.Feedback);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 分析前保存期间的新输入或关闭不会被忽略(bool close)
    {
        await using var workspace = new TestWorkspace(); await MaterialAsync(workspace); using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ScriptedTextModel(); var service = new MaterialCalibrationService(new PausingMaterialStore(new MaterialStore(workspace.Paths), entered, release), workspace.Connections, new(fake, new ModelRequestStore(workspace.Paths)));
        await using var panel = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing); await panel.InitializeAsync(); panel.Text = SourceText; panel.SelectedConnection = Assert.Single(panel.Connections);
        var operation = panel.AnalyzeMaterialCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Task<bool>? closing = null;
        try { if (close) closing = panel.SaveBeforeCloseAsync(); else panel.Feedback = "保存期间到达的新反馈"; }
        finally { release.Set(); }
        await operation; if (closing is not null) Assert.True(await closing);
        Assert.Empty(fake.Requests);
        if (!close) { Assert.Equal("保存期间到达的新反馈", panel.Feedback); Assert.True(panel.IsDirty); await panel.SaveMaterialCommand.ExecuteAsync(null); }
        Assert.Equal(panel.Feedback, Assert.Single(await service.ListAsync()).Feedback);
    }
    [Fact]
    public void 分析契约的总输出上限与规范存储容量一致()
    {
        var material = MaterialDocument.Create() with { Text = SourceText }; var card = Analysis(MaterialPurpose.Methods).Methods[0] with { Steps = Enumerable.Repeat(new string('字', 1600), 10).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => MaterialRules.ValidateAnalysis(material, new([card, card with { Name = "第二张" }], [])));
    }
    private sealed class PausingMaterialStore(IMaterialStore inner, TaskCompletionSource entered, ManualResetEventSlim release) : IMaterialStore
    {
        private int writes; public IReadOnlyList<MaterialDocument> List() => inner.List(); public MaterialDocument Read(Guid id) => inner.Read(id);
        public MaterialDocument Save(MaterialDocument material) { if (Interlocked.Increment(ref writes) == 1) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } return inner.Save(material); }
    }

    [Fact]
    public void 引用中的近义字替换不能冒充逐字材料证据()
    {
        var material = MaterialDocument.Create() with { Text = SourceText }; var analysis = Analysis(MaterialPurpose.Methods);
        analysis = analysis with { Methods = analysis.Methods.SetItem(0, analysis.Methods[0] with { Evidence = [new(1, "先建立期待，再制造障碍，最后兑现", false)] }) };
        Assert.Throws<InvalidDataException>(() => MaterialRules.ValidateAnalysis(material, analysis));
    }

    [Fact]
    public async Task 作者修改试写依据后立即标记旧样稿仅供参考()
    {
        await using var workspace = new TestWorkspace(); var material = await MaterialAsync(workspace); var service = Service(workspace, new ScriptedTextModel(_ => ChapterGenerationTests.Response(ChapterGenerationTests.Json(Analysis(MaterialPurpose.Methods)))));
        material = await service.AnalyzeAsync(material, default); material = await service.SaveAsync(material with { Trial = Trial(material), TrialStamp = material.SampleStamp });
        await using var panel = new MaterialCalibrationPanel(service, workspace.Connections, workspace.Templates, workspace.Closing); await panel.InitializeAsync(); panel.SelectedMaterial = Assert.Single(panel.Materials); await panel.ReadMaterialCommand.ExecuteAsync(null);
        panel.Style = "作者调整了文风"; Assert.Contains("依据已变化", panel.TrialPreview); Assert.True(panel.IsDirty); await panel.SaveMaterialCommand.ExecuteAsync(null);
    }

}
