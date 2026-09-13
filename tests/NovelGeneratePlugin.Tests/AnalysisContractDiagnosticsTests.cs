using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class AnalysisContractDiagnosticsTests
{
    private static ChunkAnalysisContract Contract(bool bounded = true)
    {
        var input = ReferenceSourceTests.Create(); var chunk = input.Chunks[0];
        return new(input.Source, chunk, Guid.NewGuid(), "test", ChunkAnalysisContract.Passages(input.Source, chunk), true, bounded);
    }

    [Theory]
    [InlineData("Entities")]
    [InlineData("Findings")]
    [InlineData("Gaps")]
    public void 证据八条通过超出后报告准确字段和实际数量(string section)
    {
        var json = JsonNode.Parse(AnalysisRecoveryTests.Good().Text)!;
        json[section]![0]!["Evidence"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(new PassageReference(2), 8).ToArray());
        var contract = Contract(); ModelRequestService.ValidateJson(json.ToJsonString(), contract);
        json[section]![0]!["Evidence"]!.AsArray().Add(JsonSerializer.SerializeToNode(new PassageReference(2)));
        var raw = json.ToJsonString();
        var error = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(raw, contract));
        Assert.Equal(ModelDiagnosticCode.ContractMismatch, error.Diagnostic!.Code);
        Assert.Equal($"$.{section}[0].Evidence", error.Diagnostic.Path);
        Assert.Equal(new ModelContractIssue(ModelContractRule.ArrayCount, 9, 1, 8), error.Diagnostic.ContractIssue);
        Assert.Contains("实际 9", error.Message);
        Assert.DoesNotContain("林远", error.Message);
        Assert.Equal(9, json[section]![0]!["Evidence"]!.AsArray().Count); // 不能截断候选后伪装为原响应通过。
    }

    [Fact]
    public void 新契约明确声明证据数量字段容量和有效段号()
    {
        using var schema = JsonDocument.Parse(Contract().JsonSchema);
        var root = schema.RootElement.GetProperty("properties");
        Assert.Equal(40, root.GetProperty("Entities").GetProperty("maxItems").GetInt32());
        Assert.Equal(64, root.GetProperty("Findings").GetProperty("maxItems").GetInt32());
        Assert.Equal(24, root.GetProperty("Gaps").GetProperty("maxItems").GetInt32());
        Assert.Equal(3000, root.GetProperty("Summary").GetProperty("maxLength").GetInt32());
        foreach (var name in new[] { "Entities", "Findings", "Gaps" })
        {
            var evidence = root.GetProperty(name).GetProperty("items").GetProperty("properties").GetProperty("Evidence");
            Assert.Equal(1, evidence.GetProperty("minItems").GetInt32()); Assert.Equal(8, evidence.GetProperty("maxItems").GetInt32());
            var passage = evidence.GetProperty("items").GetProperty("properties").GetProperty("Passage");
            Assert.Equal(1, passage.GetProperty("minimum").GetInt32()); Assert.Equal(4, passage.GetProperty("maximum").GetInt32());
        }
        using var legacy = JsonDocument.Parse(Contract(false).JsonSchema);
        Assert.False(legacy.RootElement.GetProperty("properties").GetProperty("Entities").TryGetProperty("maxItems", out _));
    }

    [Fact]
    public void 段号越界和文本超限提供各自规则且缺口证据仍可省略()
    {
        var contract = Contract(); var json = JsonNode.Parse(AnalysisRecoveryTests.Good().Text)!;
        ModelRequestService.ValidateJson(json.ToJsonString(), contract);
        json["Entities"]![0]!["Evidence"]![0]!["Passage"] = 999;
        var passage = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(json.ToJsonString(), contract));
        Assert.Equal("$.Entities[0].Evidence[0].Passage", passage.Diagnostic!.Path);
        Assert.Equal(new ModelContractIssue(ModelContractRule.PassageNumber, 999, 1, 4), passage.Diagnostic.ContractIssue);
        json["Entities"]![0]!["Evidence"]![0]!["Passage"] = 2;
        json["Summary"] = new string('甲', 3001);
        var length = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(json.ToJsonString(), contract));
        Assert.Equal("$.Summary", length.Diagnostic!.Path);
        Assert.Equal(ModelContractRule.TextLength, length.Diagnostic.ContractIssue!.Rule);
        json["Summary"] = "有效摘要"; json["Gaps"]![0]!["Evidence"] = new JsonArray();
        var empty = Assert.Throws<ModelRequestException>(() => ModelRequestService.ValidateJson(json.ToJsonString(), contract));
        Assert.Equal(new ModelContractIssue(ModelContractRule.ArrayCount, 0, 1, 8), empty.Diagnostic!.ContractIssue);
    }

    [Fact]
    public async Task 证据超限只纠正一次并把位置与数量传给模型和账本()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ =>
        {
            var json = JsonNode.Parse(AnalysisRecoveryTests.Good().Text)!;
            json["Entities"]![0]!["Evidence"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(new PassageReference(2), 11).ToArray());
            return AnalysisRecoveryTests.Good() with { Text = json.ToJsonString() };
        }, request =>
        {
            Assert.Contains("$.Entities[0].Evidence", request.SystemPrompt);
            Assert.Contains("实际 11", request.SystemPrompt); Assert.Contains("不能超过8条", request.SystemPrompt);
            return AnalysisRecoveryTests.Good();
        });
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model); var run = await fixture.Create();
        var result = await fixture.Service.ExecuteAsync(run.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, result.State); Assert.Equal(2, model.Requests.Count);
        var failed = Assert.Single(fixture.Service.Usage(run.Id), e => e.Diagnostic?.ContractIssue is not null);
        Assert.Equal(11, failed.Diagnostic!.ContractIssue!.Actual); Assert.True(failed.ResponseComplete);
        Assert.Equal(600, fixture.Service.Usage(run.Id).Sum(e => e.ChargedTokens));
    }

    [Fact]
    public async Task 升级提示的修订保留旧版成功节点只请求未完成节点()
    {
        await using var workspace = new TestWorkspace();
        var model = new ScriptedTextModel(_ => AnalysisRecoveryTests.Good(), _ => throw new IOException("断流"),
            request => { Assert.Contains("不能超过8条", request.SystemPrompt); return AnalysisRecoveryTests.Good(); });
        var fixture = new AnalysisRecoveryTests.Fixture(workspace, model); var seed = await fixture.Create(2); var id = Guid.NewGuid();
        var legacy = seed with
        {
            Id = id,
            Budget = seed.Budget with { Id = id },
            Nodes = [.. seed.Nodes.Select(n => n with { OperationId = Guid.NewGuid(), ExtractionPromptVersion = "v4" })]
        };
        fixture.Runs.Create(legacy);
        var stopped = await fixture.Service.ExecuteAsync(id, new(), null, default);
        var stamp = stopped.Nodes[0].InputStamp;
        fixture.Service.Resume(id, true, 100, 8000000);
        var revision = await fixture.Service.CreateAsync(seed.BookId, ConnectionService.Bind(seed.Connection.Connection),
            100, 8000000, seed.ReportReserve, default, previousRunId: id);
        var result = await fixture.Service.ExecuteAsync(revision.Id, new(), null, default);
        Assert.Equal(AnalysisRunState.ExtractionCompleted, result.State);
        Assert.Equal("v4", result.Nodes[0].ExtractionPromptVersion); Assert.Equal(stamp, result.Nodes[0].InputStamp);
        Assert.Equal("v5", result.Nodes[1].ExtractionPromptVersion); Assert.Equal(3, model.Requests.Count);
    }
}
