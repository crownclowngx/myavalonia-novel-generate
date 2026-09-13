using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class AnalysisCapacityTests
{
    private static TextModelResponse Good() => AnalysisRecoveryTests.Good();
    private static TextModelResponse Truncated(bool known = true) => new("{", ModelCompletion.Truncated, known ? new(100, 200) : new(null, null));
    private static async Task<AnalysisRun> Large(AnalysisRecoveryTests.Fixture fixture, int requests = 100, AnalysisCapacityOptions? capacity = null, bool prefix = true, long tokens = 8000000)
    {
        var seed = await fixture.Create();
        var source = ReferenceSourceTests.Create((prefix ? "第1章\n林远走进城门。\n" : "") + "第2章\n" + string.Concat(Enumerable.Repeat("林远走进城门。\n不要回头。\n", 120)));
        fixture.Sources.Import(NovelTextPartitioner.Partition(source.Source, source.Book.Name, new(6000, 0), default));
        return await fixture.Service.CreateAsync(source.Book.Id, ConnectionService.Bind(seed.Connection.Connection), requests, tokens,
            new(1, 10000), default, capacity: capacity);
    }
    private sealed class Progress(Action<AnalysisRun> report) : IProgress<AnalysisRun> { public void Report(AnalysisRun value) => report(value); }

    [Fact]
    public async Task 已知截断只拆问题单元暂停重开后不重跑前面成果()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Good(), _ => Truncated(), _ => Good(), _ => Good());
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model); var run = await Large(fixture);
        var control = new AnalysisRunControl();
        var paused = await fixture.Service.ExecuteAsync(run.Id, control, new Progress(r => { if (!r.Splits.IsEmpty) control.Pause(); }), default);
        Assert.Equal(AnalysisRunState.Paused, paused.State); Assert.Equal(2, model.Requests.Count);
        var split = Assert.Single(paused.Splits); Assert.Equal(run.Chunks[1], split.Original);
        Assert.Equal(run.Chunks[0], paused.Chunks[0]); Assert.Equal(AnalysisNodeState.Completed, paused.Nodes[0].State);
        fixture.Service.Resume(run.Id, false, 100, 8000000);
        var finished = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, finished.State); Assert.Equal(4, model.Requests.Count);
        Assert.Equal(run.Chunks.Sum(c => c.Body.Length), finished.Chunks.Sum(c => c.Body.Length));
        Assert.Equal(1200, fixture.Service.Usage(run.Id).Sum(e => e.ChargedTokens));
        (fixture.Sources.Read(run.BookId) with { Chunks = finished.Chunks }).Validate();
    }

    [Theory]
    [InlineData(true, 3, 8000000)]
    [InlineData(false, 100, 8000000)]
    [InlineData(true, 100, 130000)]
    public async Task 剩余请求或费用不足以及未知费用不能自动拆分(bool known, int requests, long tokens)
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Truncated(known)); var fixture = new AnalysisRecoveryTests.Fixture(workspace, model);
        var run = await Large(fixture, requests, prefix: false, tokens: tokens);
        var stopped = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.NeedsAttention, stopped.State); Assert.Empty(stopped.Splits); Assert.Single(model.Requests);
        Assert.False(Assert.Single(fixture.Service.Usage(run.Id)).RetryAcknowledged);
    }

    [Fact]
    public async Task 持续截断受到拆分深度限制而不会无限追加()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(Enumerable.Repeat<Func<TextModelRequest, TextModelResponse>>(_ => Truncated(), 8).ToArray());
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model);
        var run = await Large(fixture, capacity: new(MaximumSplitDepth: 2), prefix: false);
        var stopped = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.NeedsAttention, stopped.State);
        Assert.Equal(2, stopped.Splits.Length); Assert.Equal(3, model.Requests.Count); Assert.All(stopped.Splits, s => Assert.InRange(s.Depth, 1, 2));
        await fixture.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(3, model.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 拆分保存失败后重开只恢复子单元不重发父请求(bool throughResume)
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Truncated(), _ => Good(), _ => Good());
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model); var run = await Large(fixture, prefix: false);
        var service = new NovelAnalysisRunService(fixture.Sources, new FailSplitOnce(fixture.Runs), workspace.Connections, fixture.Requests, new NovelAnalysisNodePreparer());
        var stopped = await service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.NeedsAttention, stopped.State); Assert.Empty(stopped.Splits); Assert.Single(model.Requests);
        Assert.True(Assert.Single(fixture.Service.Usage(run.Id)).RetryAcknowledged);
        if (throughResume) fixture.Service.Resume(run.Id, false, run.Budget.MaximumRequests, run.Budget.MaximumTokens);
        var recovered = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, recovered.State); Assert.Single(recovered.Splits);
        Assert.Equal(3, model.Requests.Count); Assert.Equal(900, fixture.Service.Usage(run.Id).Sum(e => e.ChargedTokens));
    }

    /// <summary>模拟费用复核已落盘、运行拆分事务尚未提交时磁盘失败；恢复不能依赖两库同时成功。</summary>
    private sealed class FailSplitOnce(IAnalysisRunStore inner) : IAnalysisRunStore
    {
        private bool failed;
        public IDisposable Acquire(Guid id) => inner.Acquire(id);
        public void Create(AnalysisRun run) => inner.Create(run);
        public AnalysisRun Read(Guid id) => inner.Read(id);
        public IReadOnlyList<AnalysisRun> List(Guid id) => inner.List(id);
        public AnalysisNodeResult? ReadResult(Guid id, string key) => inner.ReadResult(id, key);
        public AnalysisNodeResult? FindCached(string stamp) => inner.FindCached(stamp);
        public void Save(AnalysisRun run, long version, AnalysisNodeResult? result = null)
        {
            if (!failed && !run.Splits.IsEmpty) { failed = true; throw new IOException("模拟拆分事务保存失败"); }
            inner.Save(run, version, result);
        }
    }

    [Fact]
    public void 局部二分保留全部字符且不拆代理对或改变其他章节()
    {
        var source = ReferenceSourceTests.Create("第1章\n" + new string('甲', 397) + "😀" + new string('乙', 402) + "\n第2章\n尾声");
        var input = NovelTextPartitioner.Partition(source.Source, source.Book.Name, new(6000, 10), default);
        var divided = NovelTextPartitioner.Split(input, input.Chunks[0].Id, 200); divided.Validate();
        Assert.Equal(input.Source.Text, string.Concat(divided.Chunks.Select(c => source.Source.Text.Substring(c.Body.Start, c.Body.Length))));
        Assert.Equal(input.Chunks[1].Id, divided.Chunks[^1].Id); Assert.Equal(input.Chunks[1].Body, divided.Chunks[^1].Body);
        Assert.All(divided.Chunks, c => { c.Body.Validate(source.Source.Text); c.Context.Validate(source.Source.Text); });
    }

    [Fact]
    public async Task 输入预检先拆分再发送不为超限父单元记费()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(Enumerable.Repeat<Func<TextModelRequest, TextModelResponse>>(r =>
        { Assert.False(ModelInputCapacity.Estimate(r).Exceeded); return Good(); }, 16).ToArray());
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model); var seed = await Large(fixture, prefix: false);
        var source = fixture.Sources.Read(seed.BookId); var node = seed.Nodes[0];
        var preparer = new NovelAnalysisNodePreparer();
        var full = ModelInputCapacity.Estimate(preparer.Prepare(seed, source, node, new Dictionary<string, AnalysisNodeResult>()).Request);
        var limit = full.TotalReservation - 1500;
        var run = await fixture.Service.CreateAsync(seed.BookId, ConnectionService.Bind(seed.Connection.Connection), 100, 8000000, seed.ReportReserve,
            default, capacity: new(limit));
        var finished = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, finished.State); Assert.NotEmpty(finished.Splits);
        Assert.All(finished.Splits, s => Assert.Equal(ModelDiagnosticCode.InputLimit, s.Reason));
        Assert.Equal(finished.Chunks.Length, model.Requests.Count);
        Assert.DoesNotContain(fixture.Service.Usage(run.Id), e => finished.Splits.Any(s => s.Parent.OperationId == e.Id));
    }

    [Fact]
    public async Task 单次容量和总费用预算独立且上限不能抬高已知模型窗口()
    {
        await using var workspace = new TestWorkspace();
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, new ScriptedTextModel(_ => Good())); var run = await fixture.Create();
        var prepared = new NovelAnalysisNodePreparer().Prepare(run, fixture.Sources.Read(run.BookId), run.Nodes[0], new Dictionary<string, AnalysisNodeResult>());
        var small = prepared.Request with { ContextTokenLimit = 8192 };
        Assert.Equal(ModelDiagnosticCode.InputLimit, Assert.Throws<ModelRequestException>(() => ModelInputCapacity.Check(small)).Diagnostic!.Code);
        Assert.Equal(1000000, ModelInputCapacity.Estimate(prepared.Request with { ContextTokenLimit = 2000000 }).ContextTokens);
        Assert.Equal(ModelRequestService.EstimateReservation(prepared.Request), ModelInputCapacity.Estimate(prepared.Request).TotalReservation);
    }
}
