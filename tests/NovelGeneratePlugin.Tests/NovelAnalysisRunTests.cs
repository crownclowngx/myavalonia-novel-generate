using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class NovelAnalysisRunTests
{
    private sealed class Context(TestWorkspace workspace, ITextModel model)
    {
        public ReferenceSourceStore Sources { get; } = new(workspace.Paths);
        public AnalysisRunStore Store { get; } = new(workspace.Paths);
        public ModelRequestService Requests { get; } = new(model, new ModelRequestStore(workspace.Paths));
        public NovelAnalysisRunService Service => new(Sources, Store, workspace.Connections, Requests, new NovelAnalysisNodePreparer());
        public async Task<AnalysisRun> Create(int count = 2, int maximumRequests = 12, long tokens = 500000, AnalysisStageReserve? reserve = null)
        {
            var input = ReferenceSourceTests.Create(string.Concat(Enumerable.Range(1, count).Select(i => $"第{i}章\n林远走进城门。\n不要回头。\n")));
            var partitioned = NovelTextPartitioner.Partition(input.Source, input.Book.Name, new(6000, 0), default);
            Sources.Import(partitioned);
            var preset = new ModelPreset("deepseek-flash", 16384, "none");
            var connection = await workspace.Connections.SaveAsync(null, new("全文测试", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
            return await Service.CreateAsync(input.Book.Id, ConnectionService.Bind(connection), maximumRequests, tokens, reserve ?? new(2, 50000), default);
        }
    }
    private static ScriptedTextModel Model(Func<TextModelRequest, TextModelResponse> step) => new(Enumerable.Repeat(step, 3).ToArray());
    private static TextModelResponse Response() => new(NovelChunkAnalysisTests.Json(NovelChunkAnalysisTests.Output()), ModelCompletion.Complete, new(100, 100));
    private sealed class Progress(Action<AnalysisRun> report) : IProgress<AnalysisRun> { public void Report(AnalysisRun value) => report(value); }

    [Fact]
    public async Task 全文共用预算并按正文覆盖且重复恢复不重复请求()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response(); })); var run = await context.Create();
        var completed = await context.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, completed.State); Assert.Equal(2, calls);
        Assert.All(context.Requests.List(run.Id), e => Assert.Equal(run.Id, e.BudgetId));
        var results = context.Service.ReadExtractions(run.Id); Assert.Equal(2, results.Count);
        var input = context.Sources.Read(run.BookId);
        Assert.Equal(input.Source.Text.Length, input.Chunks.Where(c => results.Any(r => r.ChunkId == c.Id)).Sum(c => c.Body.Length));
        await context.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 节点事务失败后重开从已完成账本恢复且只采纳一次()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response(); })); var run = await context.Create(1);
        Sql(context.Store.DatabasePath, "CREATE TRIGGER fail_result BEFORE INSERT ON results BEGIN SELECT RAISE(ABORT,'injected'); END");
        var stopped = await context.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.NeedsAttention, stopped.State); Assert.Equal(AnalysisNodeState.Running, stopped.Nodes[0].State);
        Assert.Null(context.Store.ReadResult(run.Id, run.Nodes[0].Key)); Assert.Equal(RequestState.Completed, Assert.Single(context.Requests.List(run.Id)).State);
        Sql(context.Store.DatabasePath, "DROP TRIGGER fail_result");
        var restored = await context.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, restored.State); Assert.Equal(1, calls);
        Assert.Single(context.Service.ReadExtractions(run.Id));
    }

    [Fact]
    public async Task 已存操作但尚未预留的中断可以安全发送一次()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response(); })); var run = await context.Create(1);
        var prepared = new NovelAnalysisNodePreparer().Prepare(run, context.Sources.Read(run.BookId), run.Nodes[0], new Dictionary<string, AnalysisNodeResult>());
        context.Store.Save(run with { Nodes = [run.Nodes[0] with { State = AnalysisNodeState.Running, InputStamp = prepared.InputStamp }], State = AnalysisRunState.Running, Version = 2 }, 1);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(run.Id, new(), null, default)).State); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ModelFailure.RateLimit)]
    [InlineData(ModelFailure.Timeout)]
    [InlineData(ModelFailure.Service)]
    public async Task 失败不自动重发且明确重试保留旧用量(ModelFailure failure)
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => ++calls == 1 ? throw new ModelRequestException(failure, "测试失败") : Response())); var run = await context.Create(1);
        var stopped = await context.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(AnalysisRunState.NeedsAttention, stopped.State);
        await context.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => context.Service.Resume(run.Id, false, 12, 500000));
        context.Service.Resume(run.Id, true, 12, 500000);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        var entries = context.Requests.List(run.Id); Assert.Equal(2, entries.Count); Assert.True(entries[0].ChargedTokens > 0); Assert.True(entries[0].RetryAcknowledged);
        Assert.Equal(2, calls); Assert.Single(context.Service.ReadExtractions(run.Id));
    }

    [Fact]
    public async Task 暂停完成当前节点并在恢复时跳过已保存结果()
    {
        await using var workspace = new TestWorkspace(); var calls = 0; var control = new AnalysisRunControl();
        var context = new Context(workspace, Model(_ => { calls++; control.Pause(); return Response(); })); var run = await context.Create();
        Assert.Equal(AnalysisRunState.Paused, (await context.Service.ExecuteAsync(run.Id, control, null, default)).State); Assert.Equal(1, calls);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(run.Id, new(), null, default)).State); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 取消只影响本次运行且响应后的结果仍提交()
    {
        await using var workspace = new TestWorkspace(); using var cancellation = new CancellationTokenSource();
        var context = new Context(workspace, Model(_ => { cancellation.Cancel(); return Response(); })); var first = await context.Create(); var second = await context.Create(1);
        var stopped = await context.Service.ExecuteAsync(first.Id, new(), null, cancellation.Token);
        Assert.Equal(AnalysisRunState.Cancelled, stopped.State); Assert.Single(context.Service.ReadExtractions(first.Id));
        Assert.Equal(AnalysisRunState.Queued, context.Service.Read(second.Id).State);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(second.Id, new(), null, default)).State);
    }

    [Fact]
    public async Task 阶段预算耗尽前停止且保留报告额度()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response() with { Usage = new(30000, 20000) }; }));
        var run = await context.Create(2, 4, 100000, new(2, 50000));
        Assert.Equal(AnalysisRunState.NeedsAttention, (await context.Service.ExecuteAsync(run.Id, new(), null, default)).State); Assert.Equal(1, calls);
        Assert.Single(context.Requests.List(run.Id)); Assert.Single(context.Service.ReadExtractions(run.Id));
    }

    [Fact]
    public async Task 超过请求上限在建任务前拒绝且没有发送()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response(); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Create(2, 3)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task 同源同配置缓存有效而模型版本变化不能命中()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response(); })); var first = await context.Create(1);
        await context.Service.ExecuteAsync(first.Id, new(), null, default);
        var second = await context.Service.CreateAsync(first.BookId, ConnectionService.Bind(first.Connection.Connection), 12, 500000, new(2, 50000), default);
        await context.Service.ExecuteAsync(second.Id, new(), null, default); Assert.Equal(1, calls); Assert.Empty(context.Requests.List(second.Id));
        var previous = first.Connection.Connection;
        var changed = await workspace.Connections.SaveAsync(previous, previous.Settings with { Checking = previous.Settings.Checking with { MaxOutputTokens = 8192 } });
        var third = await context.Service.CreateAsync(first.BookId, ConnectionService.Bind(changed), 12, 500000, new(2, 50000), default);
        await context.Service.ExecuteAsync(third.Id, new(), null, default); Assert.Equal(2, calls);
        Assert.Single(context.Service.ReadExtractions(first.Id));
        var input = context.Sources.Read(first.BookId); var node = first.Nodes[0];
        var original = new NovelAnalysisNodePreparer().Prepare(first, input, node, new Dictionary<string, AnalysisNodeResult>());
        var chunk = input.Chunks[0] with { Body = new(1, input.Source.Text.Length - 1), Context = new(1, input.Source.Text.Length - 1) };
        Assert.NotEqual(original.InputStamp, NovelChunkAnalysisService.Prepare(input with { Chunks = [chunk] }, chunk, first.Connection, node.OperationId, node.ExtractionPromptVersion).InputStamp);
    }

    [Fact]
    public async Task 已知费用的本地校验失败可明确重校验采纳且保留失败账本()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response(); })); var run = await context.Create(1); var preparer = new RejectingPreparer();
        var service = new NovelAnalysisRunService(context.Sources, context.Store, workspace.Connections, context.Requests, preparer);
        Assert.Equal(AnalysisRunState.NeedsAttention, (await service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Throws<ModelRequestException>(() => service.AdoptReviewedCandidate(run.Id));
        preparer.Reject = false;
        Assert.Equal(AnalysisRunState.Paused, service.AdoptReviewedCandidate(run.Id).State);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Equal(1, calls); Assert.Single(service.ReadExtractions(run.Id));
        var entry = Assert.Single(service.Usage(run.Id)); Assert.Equal(RequestState.Uncertain, entry.State); Assert.True(entry.RetryAcknowledged); Assert.Equal(200, entry.ChargedTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 未知费用和截断候选不能通过重新校验采纳(bool truncated)
    {
        await using var workspace = new TestWorkspace();
        var context = new Context(workspace, Model(_ => Response() with { Usage = truncated ? new(100, 100) : new(null, null), Completion = truncated ? ModelCompletion.Truncated : ModelCompletion.Complete }));
        var run = await context.Create(1); var preparer = new RejectingPreparer();
        var service = new NovelAnalysisRunService(context.Sources, context.Store, workspace.Connections, context.Requests, preparer);
        await service.ExecuteAsync(run.Id, new(), null, default); preparer.Reject = false;
        Assert.Throws<InvalidOperationException>(() => service.AdoptReviewedCandidate(run.Id)); Assert.Empty(service.ReadExtractions(run.Id));
    }

    private sealed class RejectingPreparer : IAnalysisNodePreparer
    {
        public bool Reject { get; set; } = true;
        public PreparedAnalysisNode Prepare(AnalysisRun run, ReferenceImport input, AnalysisNode node, IReadOnlyDictionary<string, AnalysisNodeResult> dependencies)
        {
            var prepared = new NovelAnalysisNodePreparer().Prepare(run, input, node, dependencies);
            return prepared with { Request = prepared.Request with { Contract = new Rejection(prepared.Request.Contract!, () => Reject) } };
        }
        private sealed class Rejection(IModelOutputContract contract, Func<bool> reject) : IModelOutputContract
        {
            public string JsonSchema => contract.JsonSchema;
            public void Validate(System.Text.Json.JsonElement value)
            { if (reject()) throw new InvalidDataException("模拟过严的本地校验。"); contract.Validate(value); }
        }
    }

    [Fact]
    public async Task 提示修订保留有效旧版本并只为失败单元使用新提示()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => Response(), _ => throw new IOException("断流"), request =>
        { Assert.Contains("Kind只能是Explicit", request.SystemPrompt); return Response(); });
        var context = new Context(workspace, model); var seed = await context.Create(); var id = Guid.NewGuid();
        var legacy = seed with { Id = id, Budget = seed.Budget with { Id = id }, Nodes = seed.Nodes.Select(n => n with { ExtractionPromptVersion = "v2" }).ToImmutableArray() };
        context.Store.Create(legacy); await context.Service.ExecuteAsync(legacy.Id, new(), null, default);
        var revised = await context.Service.CreateAsync(legacy.BookId, ConnectionService.Bind(legacy.Connection.Connection), 12, 500000, new(2, 50000), default, previousRunId: legacy.Id);
        Assert.Equal("v2", revised.Nodes[0].ExtractionPromptVersion); Assert.Equal("v3", revised.Nodes[1].ExtractionPromptVersion);
        context.Service.Resume(revised.Id, true, 12, 500000);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(revised.Id, new(), null, default)).State);
        Assert.Equal(3, model.Requests.Count); Assert.Single(context.Service.ReadExtractions(legacy.Id));
    }

    [Fact]
    public async Task 完整响应缺用量保留有效节点并阻止后续自动发送()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response() with { Usage = new(null, null) }; })); var run = await context.Create();
        Assert.Equal(AnalysisRunState.NeedsAttention, (await context.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Single(context.Service.ReadExtractions(run.Id)); Assert.Equal(1, calls);
        Assert.True(context.Service.Usage(run.Id)[0].ChargedTokens > 0);
        await context.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 截断保留候选且不计入覆盖()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, Model(_ => { calls++; return Response() with { Completion = ModelCompletion.Truncated }; })); var run = await context.Create(1);
        Assert.Equal(AnalysisRunState.NeedsAttention, (await context.Service.ExecuteAsync(run.Id, new(), null, default)).State);
        Assert.Empty(context.Service.ReadExtractions(run.Id)); Assert.Equal(RequestState.Truncated, context.Service.Usage(run.Id)[0].State);
        await context.Service.ExecuteAsync(run.Id, new(), null, default); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 修订切分只重算变化单元且继承旧预算与成本()
    {
        await using var workspace = new TestWorkspace(); var calls = 0;
        var context = new Context(workspace, new ScriptedTextModel(Enumerable.Repeat<Func<TextModelRequest, TextModelResponse>>(_ => { calls++; return Response(); }, 8).ToArray()));
        var seed = await context.Create(1);
        var input = ReferenceSourceTests.Create("第1章\n" + string.Concat(Enumerable.Repeat("林远走进城门。\n", 70)) + "第2章\n林远走进城门。\n");
        context.Sources.Import(NovelTextPartitioner.Partition(input.Source, "切分修订", new(6000, 0), default));
        var first = await context.Service.CreateAsync(input.Book.Id, ConnectionService.Bind(seed.Connection.Connection), 12, 500000, new(2, 50000), default);
        await context.Service.ExecuteAsync(first.Id, new(), null, default); Assert.Equal(2, calls);
        var revision = await context.Service.CreateAsync(input.Book.Id, ConnectionService.Bind(seed.Connection.Connection), 12, 500000, new(2, 50000), default, new(200, 0), first.Id);
        Assert.Equal(first.Budget.Id, revision.Budget.Id); Assert.NotEqual(first.Chunks.Length, revision.Chunks.Length);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(revision.Id, new(), null, default)).State);
        Assert.Equal(revision.Chunks.Length + 1, calls); Assert.Equal(calls, context.Service.Usage(revision.Id).Count);
        Assert.Equal(2, context.Service.ReadExtractions(first.Id).Count);
        Assert.Equal(input.Source.Text.Length, revision.Chunks.Sum(c => c.Body.Length));
    }

    [Fact]
    public async Task 运行锁陈旧版本缺失结果和损坏缓存均拒绝()
    {
        await using var workspace = new TestWorkspace(); var context = new Context(workspace, Model(_ => Response())); var run = await context.Create(1);
        using (context.Store.Acquire(run.Id)) Assert.Throws<IOException>(() => new AnalysisRunStore(workspace.Paths).Acquire(run.Id));
        context.Store.Save(run with { Version = 2 }, 1);
        Assert.Throws<InvalidOperationException>(() => context.Store.Save(run with { Version = 2 }, 1));
        Assert.Throws<InvalidDataException>(() => context.Store.Save(run with { Nodes = [run.Nodes[0] with { State = AnalysisNodeState.Completed, InputStamp = new('a', 64) }], Version = 3 }, 2));
        await context.Service.ExecuteAsync(run.Id, new(), null, default);
        Sql(context.Store.DatabasePath, "UPDATE results SET json='{}'");
        Assert.Throws<InvalidDataException>(() => context.Service.ReadExtractions(run.Id));
    }

    [Fact]
    public async Task 显示端异常不改变已完成状态()
    {
        await using var workspace = new TestWorkspace(); var context = new Context(workspace, Model(_ => Response())); var run = await context.Create(1);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, (await context.Service.ExecuteAsync(run.Id, new(), new Progress(_ => throw new IOException("view")), default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 外来库与未来版本在写入前拒绝(bool future)
    {
        await using var workspace = new TestWorkspace(); var store = new AnalysisRunStore(workspace.Paths); Directory.CreateDirectory(workspace.Paths.Root);
        if (future) { store.List(Guid.NewGuid()); Sql(store.DatabasePath, "PRAGMA user_version=99"); }
        else Sql(store.DatabasePath, "CREATE TABLE other(value TEXT)");
        var before = await File.ReadAllBytesAsync(store.DatabasePath);
        Assert.ThrowsAny<Exception>(() => store.List(Guid.NewGuid())); Assert.Equal(before, await File.ReadAllBytesAsync(store.DatabasePath));
    }
    private static void Sql(string path, string sql)
    { using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
