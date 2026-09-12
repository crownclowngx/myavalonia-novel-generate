using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Plugin;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class ReferenceSourceTests
{
    internal static ReferenceImport Create(string text = "第一章\r\n林远独自走进城门。\r\n他又听到同一句话：不要回头。\r\n不要回头。😀")
    {
        var id = Guid.NewGuid();
        var source = SourceSnapshot.Create(id, "原创测试.txt", "utf-8", Encoding.UTF8.GetBytes(text), text);
        var section = new ReferenceSection(Guid.NewGuid(), 1, "第一章", new(0, text.Length), true);
        return new(new(id, source.Id, 0, "测试小说", text.Length, DateTimeOffset.UtcNow), source,
            [section], [new(Guid.NewGuid(), section.Id, 1, section.Range, section.Range)]);
    }

    [Fact]
    public async Task 原始字节和换行在重开后保留且改名不改变来源()
    {
        await using var workspace = new TestWorkspace();
        var store = new ReferenceSourceStore(workspace.Paths); var input = Create();
        var saved = store.Import(input);
        Assert.Equal(1, saved.Version);
        var reopened = new ReferenceSourceStore(workspace.Paths).Read(saved.Id);
        Assert.Equal(input.Source.Text, reopened.Source.Text);
        Assert.Equal(input.Source.Bytes.ToArray(), reopened.Source.Bytes.ToArray());
        Assert.Equal(input.Sections.ToArray(), reopened.Sections.ToArray());
        Assert.Equal(input.Chunks.ToArray(), reopened.Chunks.ToArray());
        var renamed = store.Rename(saved.Id, saved.Version, "新书名");
        Assert.Equal(2, renamed.Version);
        Assert.Equal(input.Source.TextHash, store.ReadSource(saved.Id).TextHash);
        Assert.Throws<InvalidOperationException>(() => store.Rename(saved.Id, saved.Version, "过期改名"));
        Assert.Equal("新书名", store.ReadBook(saved.Id).Name);
    }

    [Fact]
    public async Task 插入章节失败时书目和原文一并回滚()
    {
        await using var workspace = new TestWorkspace(); var store = new ReferenceSourceStore(workspace.Paths);
        Assert.Empty(store.List());
        using (var connection = ProjectStore.Connect(store.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_section BEFORE INSERT ON reference_sections BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => store.Import(Create()));
        Assert.Empty(store.List());
        using var read = ProjectStore.Connect(store.DatabasePath);
        using var count = read.CreateCommand(); count.CommandText = "SELECT count(*) FROM reference_sources";
        Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public async Task 不覆盖重复导入且列表分页隔离两本书()
    {
        await using var workspace = new TestWorkspace(); var store = new ReferenceSourceStore(workspace.Paths);
        var first = Create("甲书正文"); var second = Create("乙书正文");
        store.Import(first); store.Import(second);
        Assert.Throws<SqliteException>(() => store.Import(first));
        Assert.Equal(second.Book.Id, Assert.Single(store.List(0, 1)).Id);
        Assert.Equal(first.Book.Id, Assert.Single(store.List(1, 1)).Id);
        Assert.Empty(store.List(2, 1));
        Assert.Equal("甲书正文", store.ReadSource(first.Book.Id).Text);
        Assert.Equal("乙书正文", store.ReadSource(second.Book.Id).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(0, 201));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 未来格式和外来数据库均拒绝修改(bool future)
    {
        await using var workspace = new TestWorkspace(); var store = new ReferenceSourceStore(workspace.Paths);
        Directory.CreateDirectory(workspace.Paths.Root);
        if (future) store.Import(Create());
        using (var connection = ProjectStore.Connect(store.DatabasePath, SqliteOpenMode.ReadWriteCreate))
        {
            using var command = connection.CreateCommand();
            command.CommandText = future ? "PRAGMA user_version=999" : "CREATE TABLE foreign_data(value TEXT); INSERT INTO foreign_data VALUES('keep')";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(store.DatabasePath);
        if (future) Assert.Throws<NotSupportedException>(() => store.Import(Create()));
        else Assert.Throws<InvalidDataException>(() => store.Import(Create()));
        Assert.Equal(before, File.ReadAllBytes(store.DatabasePath));
    }

    [Fact]
    public async Task 来源损坏与分块遗漏不能作为有效输入读取()
    {
        await using var workspace = new TestWorkspace(); var store = new ReferenceSourceStore(workspace.Paths);
        var first = store.Import(Create());
        using (var connection = ProjectStore.Connect(store.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM reference_chunks"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => store.Read(first.Id));
        using (var connection = ProjectStore.Connect(store.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE reference_sources SET text='篡改正文'"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => store.ReadSource(first.Id));
    }

    [Fact]
    public void 覆盖契约拒绝遗漏重叠跨书和切断代理对()
    {
        var input = Create(); input.Validate();
        var chunk = input.Chunks[0];
        Assert.Throws<InvalidDataException>(() => (input with { Chunks = [chunk with { Body = new(1, chunk.Body.Length - 1) }] }).Validate());
        Assert.Throws<InvalidDataException>(() => (input with { Chunks = [chunk, chunk with { Id = Guid.NewGuid(), Number = 2 }] }).Validate());
        Assert.Throws<InvalidDataException>(() => (input with { Book = input.Book with { SourceId = Guid.NewGuid() } }).Validate());
        Assert.Throws<InvalidDataException>(() => new SourceRange(0, input.Source.Text.Length - 1).Validate(input.Source.Text));
        Assert.Throws<InvalidDataException>(() => new SourceRange(int.MaxValue, 2).Validate(input.Source.Text));
    }

    [Fact]
    public void 重叠上下文不扩大正文覆盖且排除章节必须明确()
    {
        var input = Create("甲乙丙丁"); var section = input.Sections[0];
        var first = input.Chunks[0] with { Body = new(0, 2) };
        var second = first with { Id = Guid.NewGuid(), Number = 2, Body = new(2, 2) };
        (input with { Chunks = [first, second] }).Validate();
        Assert.Throws<InvalidDataException>(() => (input with { Sections = [section with { Included = false }] }).Validate());
    }

    [Fact]
    public void 引用使用精确区间且拒绝伪造引用与旧来源()
    {
        var source = Create().Source; var quote = "不要回头。";
        var offset = source.Text.LastIndexOf(quote, StringComparison.Ordinal);
        var evidence = new AnalysisEvidence(source.Id, source.TextHash, new(offset, quote.Length), quote);
        evidence.Validate(source);
        Assert.Throws<InvalidDataException>(() => evidence.Validate(source, new(0, offset)));
        Assert.Throws<InvalidDataException>(() => (evidence with { Quote = "不要转头。" }).Validate(source));
        Assert.Throws<InvalidDataException>(() => (evidence with { TextHash = "old" }).Validate(source));
        Assert.Throws<InvalidDataException>(() => (evidence with { SourceId = Guid.NewGuid() }).Validate(source));
        var finding = new AnalysisFinding(Guid.NewGuid(), AnalysisDimension.Plot, "警告", "警告重复出现", AnalysisStatementKind.Inferred, [evidence]);
        finding.Validate(source);
        Assert.Throws<InvalidDataException>(() => (finding with { Evidence = [] }).Validate(source));
    }

    [Fact]
    public void 容量与哈希不可绕过导入边界()
    {
        var input = Create();
        Assert.Throws<InvalidDataException>(() => (input.Source with { Bytes = [] }).Validate());
        Assert.Throws<InvalidDataException>(() => (input.Source with { ByteHash = "bad" }).Validate());
        Assert.Throws<InvalidDataException>(() => (input.Source with { Text = new string('字', AnalysisLimits.MaximumCharacters + 1) }).Validate());
        Assert.Throws<InvalidDataException>(() => (input.Source with { FileName = "D:\\private\\book.txt" }).Validate());
    }

    [Fact]
    public async Task 生产组合使用独立来源存储且注册不访问作者文件()
    {
        await using var workspace = new TestWorkspace();
        var services = new ServiceCollection().AddSingleton(workspace.Paths).AddNovelGeneratePluginServices();
        using var provider = services.BuildServiceProvider();
        var store = Assert.IsType<ReferenceSourceStore>(provider.GetRequiredService<IReferenceSourceStore>());
        Assert.False(File.Exists(store.DatabasePath));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReferenceSourceStore));
        Assert.Equal("reference-analysis.db", Path.GetFileName(store.DatabasePath));
    }
}
