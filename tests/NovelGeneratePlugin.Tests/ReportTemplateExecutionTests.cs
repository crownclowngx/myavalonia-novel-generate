using System.Collections.Immutable;
using System.Text.Json;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class ReportTemplateExecutionTests
{
    internal sealed class Converter : ITextModel
    {
        public List<TextModelRequest> Requests { get; } = [];
        public Func<int, TextModelRequest, TextModelResponse?>? Intercept { get; set; }
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult(Intercept?.Invoke(Requests.Count, request) ?? Response(request));
        }
        internal static TextModelResponse Response(TextModelRequest request)
        {
            using var json = JsonDocument.Parse(request.UserPrompt); var body = json.RootElement;
            var content = body.GetProperty("Content");
            var material = content.TryGetProperty("Material", out var direct) ? direct : content.GetProperty("SourceBasis");
            var sections = body.GetProperty("SelectedDimensions").EnumerateArray().Select(d =>
            {
                var dimension = Enum.Parse<ProfileDimensions>(d.GetString()!);
                var source = material.EnumerateArray().First(c => Enum.Parse<ProfileDimensions>(c.GetProperty("Targets").GetString()!).HasFlag(dimension));
                var basis = Enum.Parse<TemplateRuleBasis>(source.GetProperty("Basis").GetString()!);
                return new TemplateConversionSection(dimension, [new("以具体动作与角色选择推进冲突。", "紧张场景", basis, [source.GetProperty("Id").GetInt32()])], []);
            }).ToImmutableArray();
            return new(ReportTemplateContract.Serialize(new(sections)), ModelCompletion.Complete, new(100, 120));
        }
    }
    internal static ReportTemplateConversionService Service(TestWorkspace workspace, ITextModel model, IReportTemplateConversionStore? store = null)
    {
        var analysis = new AnalysisRunStore(workspace.Paths);
        return new(new NovelAnalysisReportService(new ReferenceSourceStore(workspace.Paths), analysis), analysis, workspace.Connections,
            new ModelRequestService(model, new ModelRequestStore(workspace.Paths)), store ?? new ReportTemplateConversionStore(workspace.Paths));
    }
    internal static async Task<ReportTemplateConversion> LongRun(TestWorkspace workspace)
    {
        var run = await ReportTemplateStorageTests.Conversion(workspace);
        var claims = Enumerable.Range(1, 24).Select(id => run.Source.Claims[0] with { Id = id, Text = new string('甲', 1800) }).ToImmutableArray();
        run = run with { Source = run.Source with { Claims = claims }, ContextTokens = 49152, Preset = run.Preset with { MaxOutputTokens = 8192 }, Budget = run.Budget with { MaximumRequests = 64, MaximumTokens = 10000000 } };
        return run with { Steps = ReportTemplateRequests.Plan(run) };
    }

    [Fact]
    public async Task 真实已保存报告转换使用独立预算且旧全文节点不重跑()
    {
        await using var workspace = new TestWorkspace(); var analysisModel = new NovelReportTests.Router();
        var context = await NovelReportTests.Setup(workspace, analysisModel); await context.Runner.ExecuteAsync(context.Run.Id, new(), null, default);
        var count = analysisModel.Requests.Count; var report = context.Reports.Read(context.Run.Id); File.Delete(context.InputPath);
        var converter = new Converter(); var service = Service(workspace, converter);
        var task = await service.PrepareAsync(context.Run.Id, ConnectionService.Bind(context.Run.Connection.Connection), "雾港模板",
            ProfileDimensions.World | ProfileDimensions.Style | ProfileDimensions.Methods, 32, 4000000);
        Assert.NotEqual(context.Run.Budget.Id, task.Budget.Id); Assert.Equal(report.Version, task.Source.ReportVersion);
        await service.CreateAsync(task); var result = await service.ExecuteAsync(task.Id, null, default);
        Assert.Equal(TemplateConversionState.CandidateSaved, result.State); Assert.Equal(3, result.Candidate!.Sections.Length);
        Assert.Equal(count, analysisModel.Requests.Count); Assert.Single(converter.Requests);
        var again = await Service(workspace, converter).ExecuteAsync(task.Id, null, default);
        Assert.Equal(result.Revision, again.Revision); Assert.Single(converter.Requests);
    }

    [Fact]
    public async Task 超长输入有界分组和合并保留所有材料且每次请求通过预检()
    {
        await using var workspace = new TestWorkspace(); var run = await LongRun(workspace);
        Assert.Contains(run.Steps, s => s.Kind == TemplateConversionStepKind.Merge);
        Assert.Equal(run.Source.Claims.Select(c => c.Id).Order(), run.Steps.Where(s => s.Kind == TemplateConversionStepKind.Extract).SelectMany(s => s.SourceIds).Order());
        var converter = new Converter(); var service = Service(workspace, converter); await service.CreateAsync(run);
        var result = await service.ExecuteAsync(run.Id, null, default); Assert.Equal(TemplateConversionState.CandidateSaved, result.State);
        Assert.Equal(run.Steps.Length, converter.Requests.Count);
        Assert.All(converter.Requests, r => { r.Validate(); Assert.False(ModelInputCapacity.Estimate(r).Exceeded); });
        Assert.Single(result.Candidate!.Sections);
    }

    [Fact]
    public async Task 完整但引用非法只自动纠正一次且费用保留在同一预算()
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var converter = new Converter { Intercept = (n, request) => n == 1 ? Converter.Response(request) with { Text = ReportTemplateContract.Serialize(new([new(ProfileDimensions.Style, [new("规范", "", TemplateRuleBasis.Inferred, [999])], [])])) } : null };
        var service = Service(workspace, converter); await service.CreateAsync(run);
        var result = await service.ExecuteAsync(run.Id, null, default);
        Assert.Equal(TemplateConversionState.CandidateSaved, result.State); Assert.Equal(2, converter.Requests.Count);
        Assert.NotEqual(converter.Requests[0].OperationId, converter.Requests[1].OperationId);
        Assert.Contains("$.Sections[0].Rules[0].Sources[0]", converter.Requests[1].SystemPrompt);
        var ledger = service.Usage(run.Id); Assert.Equal(2, ledger.Count); Assert.Equal(440, ledger.Sum(e => e.ChargedTokens));
        Assert.Single(ledger, e => e.RetryAcknowledged); Assert.Equal(1, result.Steps[0].Corrections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 响应成功后检查点失败先恢复候选而不重复请求(bool failFinalCandidate)
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var store = new FailSaveOnce(new ReportTemplateConversionStore(workspace.Paths), value =>
            failFinalCandidate ? value.State == TemplateConversionState.CandidateSaved : value.Steps.Any(s => s.State == TemplateConversionStepState.Completed));
        var converter = new Converter(); var service = Service(workspace, converter, store); await service.CreateAsync(run);
        await Assert.ThrowsAsync<TemplateConversionSaveException>(() => service.ExecuteAsync(run.Id, null, default));
        Assert.Single(converter.Requests); Assert.NotNull(store.ReadRecovery(run.Id));
        var restored = await service.ExecuteAsync(run.Id, null, default); Assert.Equal(TemplateConversionState.CandidateSaved, restored.State);
        Assert.Single(converter.Requests); Assert.Null(store.ReadRecovery(run.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 截断和未知用量不会静默重发且复核后成本仍保留(bool truncated)
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var converter = new Converter
        {
            Intercept = (n, r) => n != 1 ? null : truncated ?
            Converter.Response(r) with { Completion = ModelCompletion.Truncated } : Converter.Response(r) with { Usage = new(100, null) }
        };
        var service = Service(workspace, converter); await service.CreateAsync(run);
        Assert.Equal(TemplateConversionState.NeedsAttention, (await service.ExecuteAsync(run.Id, null, default)).State);
        Assert.Equal(TemplateConversionState.NeedsAttention, (await service.ExecuteAsync(run.Id, null, default)).State); Assert.Single(converter.Requests);
        Assert.Equal(TemplateConversionState.CandidateSaved, (await service.ExecuteAsync(run.Id, null, default, acknowledgeRetry: true)).State);
        Assert.Equal(2, converter.Requests.Count); Assert.Equal(2, service.Usage(run.Id).Count);
        if (!truncated) Assert.Contains(service.Usage(run.Id), e => e.Usage.OutputTokens is null && e.ChargedTokens == e.ReservedTokens);
    }

    [Fact]
    public async Task 分组失败只重试未完成步骤且预算不足不会发送()
    {
        await using var workspace = new TestWorkspace(); var run = await LongRun(workspace);
        var converter = new Converter { Intercept = (n, _) => n == 2 ? throw new IOException("可控断流") : null };
        var service = Service(workspace, converter); await service.CreateAsync(run);
        var failed = await service.ExecuteAsync(run.Id, null, default); Assert.Equal(TemplateConversionState.NeedsAttention, failed.State);
        var first = failed.Steps[0]; Assert.Equal(TemplateConversionStepState.Completed, first.State);
        var complete = await service.ExecuteAsync(run.Id, null, default, acknowledgeRetry: true);
        Assert.Equal(TemplateConversionState.CandidateSaved, complete.State); Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(complete.Steps[0]));
        Assert.Equal(run.Steps.Length + 1, converter.Requests.Count);
        var low = await ReportTemplateStorageTests.Conversion(workspace); low = low with { Budget = low.Budget with { MaximumTokens = 1 } };
        await service.CreateAsync(low); var before = converter.Requests.Count;
        Assert.Equal(TemplateConversionState.NeedsAttention, (await service.ExecuteAsync(low.Id, null, default)).State);
        Assert.Equal(before, converter.Requests.Count);
    }

    [Fact]
    public async Task 一次纠正仍失败停止并保留两次成本()
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var converter = new Converter { Intercept = (_, request) => Converter.Response(request) with { Text = "{\"Sections\":[]}" } };
        var service = Service(workspace, converter); await service.CreateAsync(run);
        Assert.Equal(TemplateConversionState.NeedsAttention, (await service.ExecuteAsync(run.Id, null, default)).State);
        Assert.Equal(2, converter.Requests.Count); Assert.Equal(440, service.Usage(run.Id).Sum(e => e.ChargedTokens));
        await service.ExecuteAsync(run.Id, null, default); Assert.Equal(2, converter.Requests.Count);
    }

    [Fact]
    public async Task 取消在途请求保存取消状态并要求复核后继续()
    {
        await using var workspace = new TestWorkspace(); using var cancellation = new CancellationTokenSource();
        var run = await ReportTemplateStorageTests.Conversion(workspace);
        var converter = new Converter { Intercept = (n, _) => { if (n == 1) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } return null; } };
        var service = Service(workspace, converter); await service.CreateAsync(run);
        Assert.Equal(TemplateConversionState.Cancelled, (await service.ExecuteAsync(run.Id, null, cancellation.Token)).State);
        Assert.Single(converter.Requests); Assert.Equal(TemplateConversionState.CandidateSaved, (await service.ExecuteAsync(run.Id, null, default, acknowledgeRetry: true)).State);
        Assert.Equal(2, service.Usage(run.Id).Count);
    }

    [Fact]
    public async Task 检查点与恢复文件均失败不会声称成功且账本仍能离线恢复()
    {
        await using var workspace = new TestWorkspace(); var run = await ReportTemplateStorageTests.Conversion(workspace);
        var inner = new ReportTemplateConversionStore(workspace.Paths);
        var store = new FailSaveOnce(inner, value => value.Steps.Any(s => s.State == TemplateConversionStepState.Completed)) { FailRecovery = true };
        var converter = new Converter(); var service = Service(workspace, converter, store); await service.CreateAsync(run);
        var error = await Assert.ThrowsAsync<TemplateConversionSaveException>(() => service.ExecuteAsync(run.Id, null, default));
        Assert.Contains("均保存失败", error.Message); Assert.Null(inner.ReadRecovery(run.Id)); Assert.Single(converter.Requests);
        Assert.Equal(TemplateConversionState.CandidateSaved, (await Service(workspace, converter, inner).ExecuteAsync(run.Id, null, default)).State);
        Assert.Single(converter.Requests);
    }

    internal sealed class FailSaveOnce(IReportTemplateConversionStore inner, Func<ReportTemplateConversion, bool> predicate) : IReportTemplateConversionStore
    {
        private bool _failed;
        public bool FailRecovery { get; init; }
        public IDisposable Acquire(Guid id) => inner.Acquire(id);
        public void Create(ReportTemplateConversion value) => inner.Create(value);
        public ReportTemplateConversion Read(Guid id) => inner.Read(id);
        public IReadOnlyList<ReportTemplateConversion> List(Guid id, int offset = 0, int limit = 20) => inner.List(id, offset, limit);
        public void Save(ReportTemplateConversion value, long expected)
        { if (!_failed && predicate(value)) { _failed = true; throw new IOException("注入检查点失败"); } inner.Save(value, expected); }
        public void WriteRecovery(ReportTemplateConversion value)
        { if (FailRecovery) throw new IOException("注入恢复文件失败"); inner.WriteRecovery(value); }
        public ReportTemplateConversion? ReadRecovery(Guid id) => inner.ReadRecovery(id);
        public void DeleteRecovery(Guid id) => inner.DeleteRecovery(id);
    }
}
