using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
using static NovelGeneratePlugin.Tests.ChapterGenerationTests;
namespace NovelGeneratePlugin.Tests;

public sealed class EditingTests
{
    private static ChapterGenerationService Service(TestWorkspace workspace, ITextModel model) => new(workspace.Connections, new(model, new ModelRequestStore(workspace.Paths)), new ChapterWorkStore(workspace.Paths));
    private static async Task<BookProject> Accepted(TestWorkspace workspace, int count)
    {
        var book = await BookAsync(workspace); var run = Guid.NewGuid();
        for (var i = 0; i < count; i++)
        {
            var work = await Service(workspace, new ScriptedTextModel(_ => Response(Body), _ => Response(Json(Review)))).GenerateAsync(book, book.Chapters[i].Id, run, 100, 0, new(Guid.NewGuid(), 2, 500000), null, default);
            Assert.Equal(ChapterWorkState.Ready, work.State); book = ChapterGenerationRules.Commit(book, work);
        }
        return book;
    }
    [Fact]
    public async Task 选区改写保留外围文字并完整检查且拒绝迟到接受()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = Body }) };
        var fake = new ScriptedTextModel(_ => Response(Json(new SelectionReplacement("铁钥匙"))), _ => Response(Json(Review)));
        var work = await Service(workspace, fake).RewriteAsync(book, book.Chapters[0].Id, Guid.NewGuid(), 100, new(Body.IndexOf("铜钥匙", StringComparison.Ordinal), 3, "替换材质", false), new(Guid.NewGuid(), 2, 500000), null, default);
        Assert.Equal(ChapterWorkState.Ready, work.State); Assert.Equal(Body.Replace("铜钥匙", "铁钥匙"), work.Text); Assert.Equal(2, fake.Requests.Count);
        Assert.Contains(work.Text, fake.Requests[1].UserPrompt); Assert.Contains("选区改写", work.SourceDescription);
        var changed = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = Body + "新输入" }) };
        Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Commit(changed, work));
        var accepted = ChapterGenerationRules.Commit(book, work); Assert.True(EditingRules.IsReviewCurrent(accepted, Assert.Single(accepted.Revisions.History)));
    }
    [Fact]
    public async Task 手写复核只发一个检查请求且摘要和父修订一同更新()
    {
        await using var workspace = new TestWorkspace(); var book = await Accepted(workspace, 1); var parent = book.Revisions.History[0].Id;
        var fake = new ScriptedTextModel(_ => Response(Json(Review with { Summary = "重建后的摘要" })));
        var work = await Service(workspace, fake).ReviewTextAsync(book, book.Chapters[0].Id, book.Revisions.ActiveRunId!.Value, 100, new(Guid.NewGuid(), 1, 500000), null, default);
        Assert.Single(fake.Requests); Assert.Equal(ModelTask.Checking, fake.Requests[0].Configuration.Task); Assert.Equal(Body, work.Text);
        book = ChapterGenerationRules.Commit(book, work); Assert.Equal(parent, book.Revisions.History[1].ParentId); Assert.Equal("重建后的摘要", book.Chapters[0].Summary);
    }
    [Fact]
    public async Task 选区审校失败不扩大范围修正或自动采用()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = Body }) };
        var fake = new ScriptedTextModel(_ => Response(Json(new SelectionReplacement("雪"))), _ => Response(Json(Review with { LockedPlanPreserved = false })));
        var work = await Service(workspace, fake).RewriteAsync(book, book.Chapters[0].Id, Guid.NewGuid(), 100, new(Body.Length - 1, 1, "换字", false), new(Guid.NewGuid(), 2, 500000), null, default);
        Assert.Equal(ChapterWorkState.NeedsAttention, work.State); Assert.Equal(2, fake.Requests.Count); Assert.Equal(Body, book.Chapters[0].Text);
    }
    [Fact]
    public async Task 续写只追加末尾而不改前文()
    {
        await using var workspace = new TestWorkspace(); var book = await BookAsync(workspace);
        var original = Body[..90]; book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = original }) };
        var fake = new ScriptedTextModel(_ => Response(Json(new SelectionReplacement("\n门外有人敲门。"))), _ => Response(Json(Review)));
        var work = await Service(workspace, fake).RewriteAsync(book, book.Chapters[0].Id, Guid.NewGuid(), 100, new(original.Length, 0, "有人敲门", true), new(Guid.NewGuid(), 2, 500000), null, default);
        Assert.Equal(ChapterWorkState.Ready, work.State); Assert.Equal(original + "\n门外有人敲门。", work.Text);
    }
    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 99, false)]
    [InlineData(0, 0, true)]
    [InlineData(2, 1, false)]
    public void 非法选区和半个字符拒绝处理(int start, int length, bool append)
        => Assert.Throws<InvalidOperationException>(() => EditingRules.ReplaceSelection("甲😀乙", start, length, "新", append));
    [Fact]
    public void 差异保留完整字符且支持相同文本与插入()
    {
        Assert.Equal("😀", EditingRules.Difference("甲😀乙", "甲😁乙").Before);
        Assert.Equal("新", EditingRules.Difference("甲乙", "甲新乙").After);
        Assert.Equal("正文相同。", EditingRules.Difference("相同", "相同").Describe());
    }
    [Fact]
    public async Task 零事实章节的规范变化也阻止旧通过报告定稿和续写()
    {
        await using var workspace = new TestWorkspace(); var book = await Accepted(workspace, 1); var original = book.Revisions;
        book = book with { Profile = book.Profile with { Style = "新的叙事风格" } };
        Assert.False(EditingRules.IsReviewCurrent(book, book.Revisions.History[0])); Assert.Equal(3, EditingRules.Impacts(book).Length);
        Assert.Throws<InvalidOperationException>(() => EditingRules.FinalizeRange(book, 1, 1, true));
        book = PlanningRules.MarkReady(book, book.Chapters[1].Id);
        Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Preflight(book, book.Chapters[1].Id, book.Revisions.ActiveRunId!.Value));
        Assert.Equal(original, book.Revisions);
    }
    [Fact]
    public async Task 批量定稿缺章或末章失效时整个账本不变()
    {
        await using var workspace = new TestWorkspace(); var book = await Accepted(workspace, 2);
        await using var session = await workspace.Sessions.CreateAsync(workspace.ProjectPath(), book);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitRevisionChangeAsync(b => EditingRules.FinalizeRange(b, 1, 3, true)));
        Assert.All(workspace.Store.Read(session.Path).Project.Revisions.Heads, h => Assert.Null(h.FormalId));
        var changed = book with { Chapters = book.Chapters.SetItem(1, book.Chapters[1] with { Text = "已修改" }) };
        Assert.Throws<InvalidOperationException>(() => EditingRules.FinalizeRange(changed, 1, 2, true));
        await session.CommitRevisionChangeAsync(b => EditingRules.FinalizeRange(b, 1, 2, true));
        Assert.All(workspace.Store.Read(session.Path).Project.Revisions.Heads, h => { Assert.NotNull(h.FormalId); Assert.Null(h.WorkingId); });
    }
    [Fact]
    public async Task 正式稿过期仍逐字导出但明确提示复核且不重写历史()
    {
        await using var workspace = new TestWorkspace(); var book = await Accepted(workspace, 2);
        book = book with { Revisions = EditingRules.FinalizeRange(book, 1, 2, true) };
        book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "作者正在改前文" }) };
        var export = ManuscriptExport.Prepare(book, new(ManuscriptVersion.Formal, ManuscriptFormat.Markdown, 1, 2));
        Assert.False(export.HasWarnings); Assert.All(export.Chapters, c => { Assert.Equal(Body, c.Text); Assert.Empty(c.ReviewWarning); });
        var changed = book with { Profile = book.Profile with { Style = "新的风格" } };
        var stale = ManuscriptExport.Prepare(changed, new(ManuscriptVersion.Formal, ManuscriptFormat.Markdown, 1, 2));
        Assert.True(stale.HasWarnings); Assert.All(stale.Chapters, c => Assert.NotEmpty(c.ReviewWarning));
        Assert.Equal(2, book.Revisions.History.Length);
    }
    [Fact]
    public async Task 相同正文的新父修订也使旧候选过期()
    {
        await using var workspace = new TestWorkspace(); var book = await Accepted(workspace, 1); var run = book.Revisions.ActiveRunId!.Value;
        var service = Service(workspace, new ScriptedTextModel(_ => Response(Json(Review)), _ => Response(Json(Review))));
        var old = await service.ReviewTextAsync(book, book.Chapters[0].Id, run, 100, new(Guid.NewGuid(), 1, 500000), null, default);
        var newer = await service.ReviewTextAsync(book, book.Chapters[0].Id, run, 100, new(Guid.NewGuid(), 1, 500000), null, default);
        var updated = ChapterGenerationRules.Commit(book, newer);
        Assert.Equal(book.Chapters[0].Text, updated.Chapters[0].Text);
        Assert.Throws<InvalidOperationException>(() => ChapterGenerationRules.Commit(updated, old));
    }
    [Fact]
    public async Task 旧版通过报告不伪造新规范证明且迁移前备份()
    {
        await using var workspace = new TestWorkspace(); var book = await Accepted(workspace, 1);
        book = book with { Revisions = book.Revisions with { History = book.Revisions.History.Select(r => r with { ReviewPolicyStamp = null }).ToImmutableArray() } };
        var path = workspace.ProjectPath(); workspace.Store.Create(path, book);
        using (var connection = ProjectStore.Connect(path)) { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=8"; command.ExecuteNonQuery(); }
        var reopened = workspace.Store.Read(path).Project;
        Assert.Null(reopened.Revisions.History[0].ReviewPolicyStamp); Assert.Single(Directory.GetFiles(workspace.Root, "*.before-v9-*.noveldb"));
        Assert.Throws<InvalidOperationException>(() => EditingRules.FinalizeRange(reopened, 1, 1, true));
    }
}
