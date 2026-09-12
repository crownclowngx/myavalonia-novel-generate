using System.Net;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Models;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

/// <summary>首版组合验收穿过真实 HTTP/SSE 适配和 SQLite，补齐单层模型替身不能覆盖的协议到事务连接。</summary>
public sealed class ProductAcceptanceTests
{
    [Theory]
    [InlineData("complete", 3, 6)]
    [InlineData("length", 1, 3)]
    [InlineData("disconnect", 1, 3)]
    [InlineData("authentication", 1, 3)]
    public async Task 三章HTTP流经完整调度与数据库且异常不会跳章(string scenario, int expectedChapters, int expectedCalls)
    {
        await using var workspace = new TestWorkspace(); var book = await ApiBook(workspace);
        using var handler = new SseHandler(scenario); using var client = new HttpClient(handler); var requestStore = new ModelRequestStore(workspace.Paths);
        var requests = new ModelRequestService(new DeepSeekTextModel(workspace.Connections, client), requestStore); var works = new ChapterWorkStore(workspace.Paths); var runs = new ContinuousRunStore(workspace.Paths);
        var runner = new ContinuousRunService(new(workspace.Connections, requests), new(workspace.Connections, requests, works), requests, works, runs);
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        var run = await runner.StartAsync(session, book.Chapters[0].Id, new(3, 100, 0, 6, 500000, false), new(), null, default);
        Assert.Equal(expectedChapters, run.NextChapter); Assert.Equal(expectedCalls, handler.Calls); Assert.Equal(expectedCalls, requestStore.List(run.Budget.Id).Count);
        var stored = workspace.Store.Read(session.Path).Project; Assert.Equal(expectedChapters, stored.Revisions.History.Length); Assert.All(stored.Revisions.Heads, h => Assert.Null(h.FormalId));
        Assert.DoesNotContain("only-test-secret", JsonSerializer.Serialize(stored)); Assert.DoesNotContain("only-test-secret", JsonSerializer.Serialize(requestStore.List(run.Budget.Id)));
        if (scenario == "complete")
        {
            Assert.Equal(ContinuousRunState.Completed, run.State);
            Assert.All(stored.Revisions.History, r => Assert.True(EditingRules.IsReviewCurrent(stored, r)));
            await session.CommitRevisionChangeAsync(b => EditingRules.FinalizeRange(b, 1, 3, true));
            var export = ManuscriptExport.Prepare(workspace.Store.Read(session.Path).Project, new(ManuscriptVersion.Formal, ManuscriptFormat.Markdown, 1, 3));
            Assert.Equal(3, export.Chapters.Length); Assert.False(export.HasWarnings);
        }
        else
        {
            Assert.NotEqual(ContinuousRunState.Completed, run.State); Assert.NotNull(works.Recent(book.Id, book.Chapters[1].Id).SingleOrDefault());
            var loaded = await runner.LoadAsync(book.Id); Assert.Equal(run.Id, loaded!.Id); Assert.Equal(expectedCalls, handler.Calls);
            Assert.NotEqual(RequestState.Completed, requestStore.List(run.Budget.Id).Last().State);
        }
    }
    [Fact]
    public async Task HTTP改写即使模型说通过也不能绕过本地硬规则()
    {
        await using var workspace = new TestWorkspace(); var book = await ApiBook(workspace);
        book = WritingRuleSet.Commit(book, new(null, "禁止铁钥匙", "", WritingRuleKind.ForbiddenText, WritingRuleScope.Book, WritingRuleStrength.Hard, "铁钥匙", false, "", "验收作者", true), book.Chapters[0].Id);
        book = PlanningRules.MarkReady(book, book.Chapters[0].Id); var body = ChapterGenerationTests.Body;
        book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = body }) };
        using var handler = new SseHandler("rewrite"); using var client = new HttpClient(handler);
        var requests = new ModelRequestService(new DeepSeekTextModel(workspace.Connections, client), new ModelRequestStore(workspace.Paths));
        var service = new ChapterGenerationService(workspace.Connections, requests, new ChapterWorkStore(workspace.Paths));
        var work = await service.RewriteAsync(book, book.Chapters[0].Id, Guid.NewGuid(), 100, new(body.IndexOf("铜钥匙", StringComparison.Ordinal), 3, "改为铁钥匙", false), new(Guid.NewGuid(), 2, 500000), null, default);
        Assert.Equal(ChapterWorkState.NeedsAttention, work.State); Assert.Contains(work.Issues, i => i.Severity == ReviewSeverity.Hard && i.Evidence == "铁钥匙");
        Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Commit(book, work)); Assert.Equal(2, handler.Calls);
    }
    private static async Task<BookProject> ApiBook(TestWorkspace workspace)
    {
        var book = await ChapterGenerationTests.BookAsync(workspace); var preset = new ModelPreset("deepseek-flash", 8192, "high");
        var connection = await workspace.Connections.SaveAsync(null, new("SSE 本地验收", ModelProvider.DeepSeek, "https://api.deepseek.com", "", preset, preset, preset));
        var binding = ConnectionService.Bind(connection); await workspace.Connections.SetSecretAsync(binding, "only-test-secret", false); return book with { Connection = binding };
    }
    private sealed class SseHandler(string scenario) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal("only-test-secret", request.Headers.Authorization?.Parameter); Assert.Equal("api.deepseek.com", request.RequestUri!.Host);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            if (Calls == 3 && scenario == "authentication") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("only-test-secret 不得回显") };
            var text = Calls % 2 == 1 ? ChapterGenerationTests.Body : ChapterGenerationTests.Json(ChapterGenerationTests.Review);
            if (scenario == "rewrite" && Calls == 1) text = ChapterGenerationTests.Json(new SelectionReplacement("铁钥匙"));
            var finish = Calls == 3 && scenario == "length" ? "length" : "stop";
            var data = Event(text, null);
            if (!(Calls == 3 && scenario == "disconnect")) data += Event("", finish) + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":100}}\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
        private static string Event(string content, string? finish) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content }, finish_reason = finish } } }) + "\n\n";
    }
}
