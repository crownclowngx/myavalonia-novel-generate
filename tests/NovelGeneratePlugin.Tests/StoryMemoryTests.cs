using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class StoryMemoryTests
{
    private static BookProject Book()
    {
        var book = BookEdits.AddChapter(BookProject.Create("雾港来信", "邮差寻找旧书店"), null).Project;
        var entity = new StoryEntity(Guid.NewGuid(), StoryEntityKind.Person, "林舟", ["阿舟"], "邮差", ImmutableDictionary<string, string>.Empty);
        return book with { Story = new(1, [entity], StoryEntityDraft.From(entity)), Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = "林舟在雾港拾起铜钥匙。", Summary = "林舟获得钥匙" }) };
    }
    private static BookProject Commit(BookProject book, int index, Guid run, ImmutableArray<FactDelta> facts = default)
    {
        var chapter = book.Chapters[index]; var head = book.Revisions.Head(chapter.Id);
        var submission = new DraftSubmission(book.Id, chapter.Id, head.WorkingId, head.FormalId, RevisionRules.Hash(chapter.Text), RevisionRules.ContextStamp(book, chapter.Id),
            chapter.Text, chapter.Summary, facts.IsDefault ? [] : facts, RevisionCheck.Passed, run, Guid.NewGuid());
        return book with { Revisions = RevisionRules.CommitWorking(book, submission) };
    }
    private static StoryFactCandidate Candidate(BookProject book, StoryContext context, string text) => new(book.Id, context.ChapterId, book.Story.Entities[0].Id,
        StoryFactKind.State, "所在地", null, "雾港", "林舟在雾港", context.Stamp, RevisionRules.Hash(text))
    { Time = StoryFactTime.Established };
    private static BookProject Accepted(bool formal)
    {
        var book = Book(); var run = Guid.NewGuid(); var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, run, true);
        var fact = StoryMemory.ValidateCandidate(book, context, Candidate(book, context, book.Chapters[0].Text), book.Chapters[0].Text);
        book = Commit(book, 0, run, [fact]);
        return formal ? book with { Revisions = RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value, true) } : book;
    }
    [Fact]
    public void 中文两字人名与别名检索均返回原始来源且不读取未来章()
    {
        var book = Accepted(true); var target = book.Chapters[1].Id;
        Assert.Single(StoryMemory.Search(book, target, false, "林舟")); var hit = Assert.Single(StoryMemory.Search(book, target, false, "阿舟"));
        Assert.Equal(book.Revisions.History[0].Id, hit.RevisionId); Assert.False(hit.Working);
        book = BookEdits.AddChapter(book, target).Project;
        book = book with { Chapters = book.Chapters.SetItem(2, book.Chapters[2] with { Text = "林舟未来才会抵达雪原。" }) };
        book = Commit(book, 2, Guid.NewGuid());
        Assert.Empty(StoryMemory.Search(book, target, true, "雪原")); Assert.Single(StoryMemory.Before(book, target, true));
    }

    [Fact]
    public void 检索达到扫描容量时明确停止不伪装完整召回()
    {
        var book = Book(); book = book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = new string('甲', 4000001) }) };
        book = Commit(book, 0, Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => StoryMemory.Search(book, book.Chapters[1].Id, true, "甲"));
    }
    [Fact]
    public void 实体改名保留身份旧名检索和事实键()
    {
        var book = Accepted(true); var id = book.Story.Entities[0].Id; var key = book.Revisions.History[0].Facts[0].Key;
        book = book with { Story = (book.Story with { Editor = book.Story.Editor with { Name = "林望" } }).Commit() };
        Assert.Equal(id, book.Story.Entities[0].Id); Assert.Contains("林舟", book.Story.Entities[0].Aliases);
        Assert.Single(StoryMemory.Search(book, book.Chapters[1].Id, false, "林望")); Assert.Equal(key, book.Revisions.History[0].Facts[0].Key); book.Validate();
    }
    [Fact]
    public void 工作和正式视图隔离且放弃工作稿不污染正式记忆()
    {
        var book = Accepted(false); var target = book.Chapters[1].Id;
        Assert.Single(StoryMemory.Before(book, target, true)); Assert.Empty(StoryMemory.Before(book, target, false));
        var context = new StoryContextBuilder().Build(book, target, book.Revisions.ActiveRunId, true); Assert.Contains("雾港", context.Render());
        Assert.Throws<InvalidOperationException>(() => new StoryContextBuilder().Build(book, target, Guid.NewGuid(), true));
        book = book with { Revisions = RevisionRules.DiscardWorking(book) }; Assert.Empty(StoryMemory.Before(book, target, true)); Assert.Single(book.Revisions.History);
    }
    [Fact]
    public void 过期工作稿来源阻止装配而不是使用错误历史()
    {
        var book = Accepted(false); book = BookEdits.AddChapter(book, book.Chapters[1].Id).Project;
        book = book with { Chapters = book.Chapters.SetItem(1, book.Chapters[1] with { Text = "林舟离开书店。" }) };
        book = Commit(book, 1, book.Revisions.ActiveRunId!.Value);
        // 工作稿尚未定稿，重排在领域结构上可保存，但它的前文来源已经改变。
        book = book with { Chapters = [book.Chapters[1], book.Chapters[0], book.Chapters[2]] };
        Assert.Throws<InvalidOperationException>(() => StoryMemory.Before(book, book.Chapters[2].Id, true));
    }
    [Theory]
    [InlineData(StoryFactKind.State)]
    [InlineData(StoryFactKind.Relationship)]
    [InlineData(StoryFactKind.Event)]
    [InlineData(StoryFactKind.Possession)]
    [InlineData(StoryFactKind.Knowledge)]
    [InlineData(StoryFactKind.Foreshadow)]
    public void 基础事实类别保留证据和实体身份(StoryFactKind kind)
    {
        var book = Book(); var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, null, true); var text = book.Chapters[0].Text;
        var fact = StoryMemory.ValidateCandidate(book, context, Candidate(book, context, text) with { Kind = kind, RelatedEntityId = book.Story.Entities[0].Id }, text);
        Assert.NotEqual(fact.Key, StoryMemory.FactKey(book.Story.Entities[0].Id, kind, "所在地", Guid.NewGuid()));
        Assert.Equal(kind, fact.Kind); Assert.Equal(book.Story.Entities[0].Id, fact.EntityId); Assert.Equal("林舟在雾港", fact.Evidence);
    }
    [Theory]
    [InlineData("book")]
    [InlineData("chapter")]
    [InlineData("entity")]
    [InlineData("stamp")]
    [InlineData("text")]
    [InlineData("old")]
    [InlineData("evidence")]
    [InlineData("planned")]
    [InlineData("uncertain")]
    public void 跨书未知实体过期旧值无证据和未来候选均拒绝(string defect)
    {
        var book = Book(); var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, null, true); var text = book.Chapters[0].Text;
        var candidate = Candidate(book, context, text); candidate = defect switch
        {
            "book" => candidate with { BookId = Guid.NewGuid() },
            "chapter" => candidate with { ChapterId = Guid.NewGuid() },
            "entity" => candidate with { EntityId = Guid.NewGuid() },
            "stamp" => candidate with { ContextStamp = "旧上下文" },
            "text" => candidate with { TextHash = "旧正文" },
            "old" => candidate with { ExpectedValue = "雪原" },
            "evidence" => candidate with { Evidence = "不在正文中" },
            "planned" => candidate with { Time = StoryFactTime.Planned },
            _ => candidate with { Time = StoryFactTime.Uncertain }
        };
        Assert.Throws<InvalidOperationException>(() => StoryMemory.ValidateCandidate(book, context, candidate, text));
    }
    [Fact]
    public void 触碰锁定状态与规范更新后的旧候选拒绝()
    {
        var book = Book(); var entity = book.Story.Entities[0] with { LockedFields = ImmutableDictionary<string, string>.Empty.Add("所在地", "雪原") };
        book = book with { Story = book.Story with { Entities = [entity] } }; var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, null, true);
        var candidate = Candidate(book, context, book.Chapters[0].Text);
        Assert.Throws<InvalidOperationException>(() => StoryMemory.ValidateCandidate(book, context, candidate, book.Chapters[0].Text));
        book = book with { Profile = book.Profile with { Style = "新文风" } };
        Assert.Throws<InvalidOperationException>(() => StoryMemory.ValidateCandidate(book, context, candidate, book.Chapters[0].Text));
    }
    [Fact]
    public void 硬规则和有效事实先占预算超限不静默丢弃()
    {
        var book = Accepted(true); book = book with { Profile = book.Profile with { Rules = "主角不能无故获得新能力。", Style = new string('风', 20000) } };
        var context = new StoryContextBuilder().Build(book, book.Chapters[1].Id, null, false, 5000, "林舟");
        Assert.Contains(context.Parts, p => p.Purpose == "长期规范原文" && p.Required); Assert.Contains(context.Parts, p => p.Purpose == "有效前文事实" && p.Required);
        Assert.Contains(context.Omitted, p => p.StartsWith("文风")); Assert.Equal(Encoding.UTF8.GetByteCount(context.Render()), context.Utf8Bytes); Assert.True(context.Utf8Bytes <= 5000);
        book = book with { Profile = book.Profile with { Rules = new string('规', 3000) } };
        Assert.Throws<InvalidOperationException>(() => new StoryContextBuilder().Build(book, book.Chapters[1].Id, null, false, 5000));
    }

    [Fact]
    public void 验证后修改锁定项阻止提交且提交后修改规范阻止定稿()
    {
        var book = Book(); var run = Guid.NewGuid(); var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, run, true);
        var fact = StoryMemory.ValidateCandidate(book, context, Candidate(book, context, book.Chapters[0].Text), book.Chapters[0].Text);
        var entity = book.Story.Entities[0] with { LockedFields = ImmutableDictionary<string, string>.Empty.Add("所在地", "雪原") };
        var changed = book with { Story = book.Story with { Entities = [entity] } };
        Assert.Throws<InvalidOperationException>(() => Commit(changed, 0, run, [fact]));
        book = Commit(book, 0, run, [fact]);
        book = book with { Profile = book.Profile with { Rules = "改变后的规范" } };
        Assert.Throws<InvalidOperationException>(() => RevisionRules.FinalizeChapter(book, book.Chapters[0].Id, book.Revisions.Head(book.Chapters[0].Id).WorkingId!.Value, true));
    }

    [Fact]
    public void 关系候选不能引用未知的另一实体()
    {
        var book = Book(); var context = new StoryContextBuilder().Build(book, book.Chapters[0].Id, null, true); var text = book.Chapters[0].Text;
        Assert.Throws<InvalidOperationException>(() => StoryMemory.ValidateCandidate(book, context, Candidate(book, context, text) with { Kind = StoryFactKind.Relationship }, text));
        Assert.Throws<InvalidOperationException>(() => StoryMemory.ValidateCandidate(book, context, Candidate(book, context, text) with { RelatedEntityId = Guid.NewGuid() }, text));
    }
    [Fact]
    public void 锁定字段的字典顺序不影响重开后的规范指纹()
    {
        var book = Book(); var entity = book.Story.Entities[0] with { LockedFields = ImmutableDictionary.Create<string, string>(new CollisionComparer()).Add("出生地", "雾港").Add("身份", "邮差") };
        book = book with { Story = book.Story with { Entities = [entity], Editor = StoryEntityDraft.From(entity) } };
        var stamp = StoryMemory.PolicyStamp(book, book.Chapters[0].Id, null);
        var reopened = JsonSerializer.Deserialize<BookProject>(JsonSerializer.Serialize(book))!;
        Assert.Equal(stamp, StoryMemory.PolicyStamp(reopened, reopened.Chapters[0].Id, null));
        Assert.Equal(book.Story.Editor, StoryEntityDraft.From(reopened.Story.Entities[0]));
    }
    private sealed class CollisionComparer : IEqualityComparer<string>
    { public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y); public int GetHashCode(string value) => 0; }
    [Fact]
    public void 锁定字段改名与另一项冲突不能静默覆盖()
    {
        var book = Book(); var entity = book.Story.Entities[0] with { LockedFields = ImmutableDictionary<string, string>.Empty.Add("所在地", "雾港").Add("出生地", "雪原") };
        var draft = StoryEntityDraft.From(entity); var other = entity.LockedFields.Keys.Single(k => k != draft.LockedField);
        var catalog = book.Story with { Entities = [entity], Editor = draft with { LockedField = other } };
        Assert.Throws<InvalidOperationException>(() => catalog.Commit()); Assert.Equal(2, catalog.Entities[0].LockedFields.Count);
    }
    [Fact]
    public async Task 类型化事实持久化重开与复制作品身份隔离()
    {
        await using var workspace = new TestWorkspace(); var book = Accepted(true); workspace.Store.Create(workspace.ProjectPath(), book);
        var reopened = workspace.Store.Read(workspace.ProjectPath()).Project;
        Assert.Equal(book.Revisions.History[0].Facts[0], reopened.Revisions.History[0].Facts[0]);
        Assert.Single(StoryMemory.Search(reopened, reopened.Chapters[1].Id, false, "阿舟"));
        var copied = reopened.CopyAsNew(); Assert.NotEqual(book.Id, copied.Id); copied.Validate();
        Assert.Single(StoryMemory.Search(copied, copied.Chapters[1].Id, false, "阿舟"));
    }
    [Fact]
    public async Task 第五版先备份后迁移实体目录且不添加虚构事实()
    {
        await using var workspace = new TestWorkspace(); var book = BookProject.Create("旧作品"); workspace.Store.Create(workspace.ProjectPath(), book);
        using (var connection = ProjectStore.Connect(workspace.ProjectPath()))
        {
            using var command = connection.CreateCommand(); command.CommandText = "UPDATE project SET snapshot=json_remove(snapshot,'$.Story'); PRAGMA user_version=5;"; command.ExecuteNonQuery();
        }
        var migrated = workspace.Store.Read(workspace.ProjectPath()); Assert.Empty(migrated.Project.Story.Entities); Assert.Empty(migrated.Project.Revisions.History);
        var backup = Assert.Single(Directory.GetFiles(workspace.Root, "*.before-v" + ProjectStore.SchemaVersion + "-*.noveldb"));
        using var source = ProjectStore.Connect(backup, SqliteOpenMode.ReadOnly); using var check = source.CreateCommand(); check.CommandText = "PRAGMA user_version"; Assert.Equal(5L, check.ExecuteScalar());
    }
    [Fact]
    public async Task 实体编辑草案关闭保留上下文预览更新后标过期()
    {
        await using var workspace = new TestWorkspace();
        await using (var document = workspace.CreateDocument())
        {
            await document.InitializeAsync(new NewDocumentActivation("故事测试"), default); workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
            document.EntityName = "林舟"; document.EntityAliases = "阿舟"; await document.SaveStoryEntityCommand.ExecuteAsync(null); Assert.Single(document.StoryEntities);
            await document.PreviewStoryContextCommand.ExecuteAsync(null); Assert.Contains("当前上下文", document.StoryContextStatus);
            document.ProfileWorld = "雾港禁止使用魔法"; Assert.Contains("过期", document.StoryContextStatus);
            document.EntityName = "尚未提交的改名"; Assert.False(document.CanSelectEntity); await document.SaveCommand.ExecuteAsync(null);
        }
        await using var reopened = workspace.CreateDocument(); await reopened.InitializeAsync(new NewDocumentActivation("重开"), default);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await reopened.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal("尚未提交的改名", reopened.EntityName); Assert.False(reopened.CanSelectEntity);
        reopened.DiscardEntityDraftCommand.Execute(null); Assert.Equal("林舟", reopened.EntityName); Assert.True(reopened.CanSelectEntity);
    }
}
