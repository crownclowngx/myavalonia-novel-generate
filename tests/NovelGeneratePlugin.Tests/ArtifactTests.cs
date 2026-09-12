using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class ArtifactTests
{
    private static BookProject Story(string text = "雾港的灯亮了。")
    { var book = BookProject.Create("雾港来信"); return book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = text }) }; }
    private static BookProject Commit(BookProject book, int index)
    {
        var chapter = book.Chapters[index]; var head = book.Revisions.Head(chapter.Id);
        var submission = new DraftSubmission(book.Id, chapter.Id, head.WorkingId, head.FormalId, RevisionRules.Hash(chapter.Text), RevisionRules.ContextStamp(book, chapter.Id), chapter.Text, "摘要", [], RevisionCheck.NotChecked, book.Revisions.ActiveRunId ?? Guid.NewGuid(), Guid.NewGuid());
        return book with { Revisions = RevisionRules.CommitWorking(book, submission) };
    }
    [Fact]
    public async Task 活跃WAL备份包含已提交正文且恢复保留身份()
    {
        await using var workspace = new TestWorkspace(); var source = workspace.ProjectPath();
        await using var session = await workspace.Sessions.CreateAsync(source, Story());
        using var keeper = ProjectStore.Connect(source);
        using var readTransaction = keeper.BeginTransaction(deferred: true);
        using var readOld = keeper.CreateCommand(); readOld.Transaction = readTransaction; readOld.CommandText = "SELECT snapshot FROM project"; readOld.ExecuteScalar();
        session.Update(session.Current with { Idea = "已经提交但仍在 WAL 中的新创意" }); await session.SaveAsync();
        Assert.True(File.Exists(source + "-wal")); Assert.True(new FileInfo(source + "-wal").Length > 0);
        var backup = workspace.ProjectPath("备份"); await workspace.Artifacts.BackupAsync(source, backup);
        var restored = workspace.ProjectPath("恢复"); await workspace.Artifacts.BackupAsync(backup, restored);
        var read = workspace.Store.Read(restored).Project;
        Assert.Equal(session.Id, read.Id); Assert.Equal(session.Current.Idea, read.Idea); Assert.Equal(session.Current.Chapters[0].Text, read.Chapters[0].Text);
    }
    [Fact]
    public async Task 源事务未提交内容不进入一致性备份()
    {
        await using var workspace = new TestWorkspace(); var original = Story(); var source = workspace.ProjectPath(); workspace.Store.Create(source, original);
        using var writer = ProjectStore.Connect(source); using var transaction = writer.BeginTransaction();
        using var update = writer.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE project SET snapshot=$json"; update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(original with { Idea = "未提交内容" })); update.ExecuteNonQuery();
        var backup = workspace.ProjectPath("事务中备份"); await workspace.Artifacts.BackupAsync(source, backup);
        Assert.Equal(original.Idea, workspace.Store.Read(backup).Project.Idea); transaction.Rollback();
    }
    [Fact]
    public async Task 旧格式备份只升级恢复文件且源保持原格式()
    {
        await using var workspace = new TestWorkspace(); var source = workspace.ProjectPath(); var original = Story(); workspace.Store.Create(source, original);
        using (var connection = ProjectStore.Connect(source))
        { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=4"; command.ExecuteNonQuery(); }
        var restored = workspace.ProjectPath("迁移恢复"); await workspace.Artifacts.BackupAsync(source, restored);
        using var sourceRead = ProjectStore.Connect(source, SqliteOpenMode.ReadOnly); using var schema = sourceRead.CreateCommand(); schema.CommandText = "PRAGMA user_version";
        Assert.Equal(4, Convert.ToInt32(schema.ExecuteScalar())); Assert.Equal(original.Id, workspace.Store.Read(restored).Project.Id);
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp*"));
    }
    [Fact]
    public async Task 占用目标与无效源失败不创建半成品备份()
    {
        await using var workspace = new TestWorkspace(); var source = workspace.ProjectPath(); workspace.Store.Create(source, Story());
        var target = workspace.ProjectPath("占用");
        using (new FileProjectLeaseProvider().Acquire(target)) await Assert.ThrowsAsync<IOException>(() => workspace.Artifacts.BackupAsync(source, target));
        Assert.False(File.Exists(target));
        var invalid = workspace.ProjectPath("无效源"); File.WriteAllText(invalid, "不是 SQLite 项目");
        await Assert.ThrowsAsync<SqliteException>(() => workspace.Artifacts.BackupAsync(invalid, target)); Assert.False(File.Exists(target));
    }
    [Fact]
    public async Task 稿件三种版本明确且工作正式不回退到编辑稿()
    {
        await using var workspace = new TestWorkspace(); var book = Commit(Story("已提交工作稿"), 0); var chapter = book.Chapters[0];
        book = book with { Revisions = RevisionRules.FinalizeChapter(book, chapter.Id, book.Revisions.Head(chapter.Id).WorkingId!.Value, true) };
        book = book with { Chapters = book.Chapters.SetItem(0, chapter with { Text = "尚未提交的编辑稿" }) };
        var editing = await workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.Editing, ManuscriptFormat.Text, 1, 1));
        var working = await workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.WorkingView, ManuscriptFormat.Text, 1, 1));
        var formal = await workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.Formal, ManuscriptFormat.Text, 1, 1));
        Assert.Contains("尚未提交的编辑稿", editing.Content); Assert.Contains("已提交工作稿", working.Content); Assert.Equal(working.Content, formal.Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Artifacts.PrepareAsync(Story(), new ExportSelection(ManuscriptVersion.Formal, ManuscriptFormat.Text, 1, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Artifacts.PrepareAsync(Story(), new ExportSelection(ManuscriptVersion.WorkingView, ManuscriptFormat.Text, 1, 1)));
    }
    [Fact]
    public async Task 导出对所选修订重新检查而不借用编辑稿检查()
    {
        await using var workspace = new TestWorkspace(); var book = Commit(Story("总而言之，他回家了。"), 0); var chapter = book.Chapters[0];
        book = book with
        {
            Revisions = RevisionRules.FinalizeChapter(book, chapter.Id, book.Revisions.Head(chapter.Id).WorkingId!.Value, true),
            Chapters = book.Chapters.SetItem(0, chapter with { Text = "门外传来脚步声。" })
        };
        book = WritingRuleSet.Commit(book, RuleDraft.Empty with { Original = "不要总结式结尾", Pattern = "总而言之" }, chapter.Id);
        var editing = await workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.Editing, ManuscriptFormat.Text, 1, 1));
        var formal = await workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.Formal, ManuscriptFormat.Text, 1, 1));
        Assert.False(editing.HasWarnings); Assert.True(formal.HasWarnings); Assert.Single(formal.Chapters[0].Check.Findings);
    }
    [Fact]
    public async Task 章节范围格式与非空白字数在实际UTF8文件正确()
    {
        await using var workspace = new TestWorkspace(); var book = BookEdits.AddVolume(Story()).Project;
        book = book with { Chapters = book.Chapters.SetItem(1, book.Chapters[1] with { Text = "甲 😀\n乙" }) };
        var prepared = await workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.Editing, ManuscriptFormat.Markdown, 2, 2));
        Assert.Equal(3, prepared.WordCount); Assert.DoesNotContain("雾港的灯", prepared.Content); Assert.Contains("## 第 2 卷", prepared.Content);
        var path = Path.Combine(workspace.Root, "正文.md"); await workspace.Artifacts.WriteAsync(prepared, path);
        var bytes = File.ReadAllBytes(path); Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal(prepared.Content, Encoding.UTF8.GetString(bytes));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Artifacts.PrepareAsync(book, new ExportSelection(ManuscriptVersion.Editing, ManuscriptFormat.Text, 2, 1)));
    }
    [Fact]
    public async Task 导出失败不破坏已有输出或作品库()
    {
        await using var workspace = new TestWorkspace(); var prepared = await workspace.Artifacts.PrepareAsync(Story(), new ExportSelection(ManuscriptVersion.Editing, ManuscriptFormat.Text, 1, 1));
        var path = Path.Combine(workspace.Root, "已有稿件.txt"); File.WriteAllText(path, "原有输出");
        await Assert.ThrowsAsync<IOException>(() => workspace.Artifacts.WriteAsync(prepared, path)); Assert.Equal("原有输出", File.ReadAllText(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Artifacts.WriteAsync(prepared, workspace.ProjectPath())); Assert.False(File.Exists(workspace.ProjectPath()));
    }
    [Fact]
    public async Task 模板交换保留草案和旧版本但导入独立身份()
    {
        await using var workspace = new TestWorkspace();
        var template = await workspace.Templates.CreatePublishedAsync(new TemplateDraft("雾港模板", ["悬疑"], new WritingProfile("雾港", "短句", "障碍", "规则"), "测试来源"));
        template = await workspace.Templates.SaveDraftAsync(template, template.Draft with { Content = template.Draft.Content with { World = "未发布的新世界" } });
        var path = Path.Combine(workspace.Root, "模板.json"); await workspace.Artifacts.ExportTemplateAsync(template.Id, path);
        var imported = await workspace.Artifacts.ImportTemplateAsync(path);
        Assert.NotEqual(template.Id, imported.Id); Assert.NotEqual(template.Versions[0].Id, imported.Versions[0].Id);
        Assert.Equal("未发布的新世界", imported.Draft.Content.World); Assert.Equal("雾港", imported.Versions[0].Content.World);
        Assert.Equal(2, (await workspace.Templates.ListAsync()).Count);
        File.WriteAllText(path, "{\"FormatVersion\":999}"); await Assert.ThrowsAsync<InvalidDataException>(() => workspace.Artifacts.ImportTemplateAsync(path));
        Assert.Equal(2, (await workspace.Templates.ListAsync()).Count);
    }
    [Fact]
    public async Task 文档导出先预览警告确认且修改后旧预览不能继续写()
    {
        await using var workspace = new TestWorkspace(); await using var document = workspace.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("导出试验"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
        document.ChapterText = "总而言之，事情结束了。"; document.RuleOriginal = "禁用总结词"; document.RulePattern = "总而言之";
        await document.SaveRuleVersionCommand.ExecuteAsync(null); await document.PreviewExportCommand.ExecuteAsync(null);
        Assert.False(document.CanWriteExport); document.AcknowledgeExportWarnings = true; Assert.True(document.CanWriteExport);
        document.ChapterText = "总而言之，事情仍未结束。"; Assert.False(document.CanWriteExport);
        await document.PreviewExportCommand.ExecuteAsync(null); Assert.False(document.AcknowledgeExportWarnings); document.AcknowledgeExportWarnings = true;
        workspace.Interaction.NextPath = Path.Combine(workspace.Root, "确认的稿件.txt"); await document.WriteExportCommand.ExecuteAsync(null);
        Assert.Contains(document.ChapterText, File.ReadAllText(workspace.Interaction.NextPath));
    }
}
