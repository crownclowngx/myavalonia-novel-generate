using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Domain;
using Xunit;
namespace NovelGeneratePlugin.Tests;

public sealed class WritingRuleTests
{
    [Fact]
    public void 过多命中不无限增长也不能伪装完整检查()
    {
        var book = Add(Story(new string('甲', 1001)), "甲");
        var check = RuleEvaluation.Check(book, book.Chapters[0].Id, null);
        Assert.False(check.Complete); Assert.True(check.HasHardFailure); Assert.Equal(1000, check.Findings.Length);
    }
    [Fact]
    public async Task 未提交规则草案禁止切换且明确放弃后才能打开其他规则()
    {
        await using var workspace = new TestWorkspace();
        var book = Add(Add(Story("待检查正文"), "甲"), "乙"); workspace.Store.Create(workspace.ProjectPath(), book);
        await using var document = workspace.CreateDocument(); await document.InitializeAsync(new NewDocumentActivation("草案切换"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await document.OpenProjectCommand.ExecuteAsync(null);
        document.SelectedWritingRule = document.WritingRules[0]; document.RuleOriginal = "必须保留的新原文";
        Assert.False(document.CanSelectRule); var selected = document.SelectedWritingRule;
        document.SelectedWritingRule = document.WritingRules[1]; Assert.Equal(selected, document.SelectedWritingRule); Assert.Equal("必须保留的新原文", document.RuleOriginal);
        document.NewRuleCommand.Execute(null); Assert.Equal("必须保留的新原文", document.RuleOriginal);
        document.DiscardRuleDraftCommand.Execute(null); Assert.True(document.CanSelectRule);
        document.SelectedWritingRule = document.WritingRules[1]; Assert.Equal("测试规则：乙", document.RuleOriginal);
    }
    private static BookProject Story(string text)
    { var book = BookProject.Create("规则试验"); return book with { Chapters = book.Chapters.SetItem(0, book.Chapters[0] with { Text = text }) }; }
    private static BookProject Add(BookProject book, string pattern, WritingRuleKind kind = WritingRuleKind.ForbiddenText,
        bool ignore = false, string exceptions = "", WritingRuleScope scope = WritingRuleScope.Book, WritingRuleStrength strength = WritingRuleStrength.Hard)
        => WritingRuleSet.Commit(book, RuleDraft.Empty with { Original = "测试规则：" + pattern, Pattern = pattern, Kind = kind, IgnoreSeparators = ignore, ExceptionsText = exceptions, Scope = scope, Strength = strength }, book.Chapters[0].Id);
    [Fact]
    public void 跨标点换行检测保留包含表情的原文位置且不改写正文()
    {
        const string text = "😀他停下。总，\n而 言 之，故事结束。";
        var book = Add(Story(text), "总而言之", ignore: true); var finding = Assert.Single(RuleEvaluation.Check(book, book.Chapters[0].Id, null).Findings);
        Assert.Equal(text.IndexOf('总'), finding.Start); Assert.Equal("总，\n而 言 之", text.Substring(finding.Start, finding.Length));
        Assert.Equal(text, book.Chapters[0].Text);
        var exact = Add(Story(text), "总而言之"); Assert.Empty(RuleEvaluation.Check(exact, exact.Chapters[0].Id, null).Findings);
    }
    [Fact]
    public void 例外只豁免完整覆盖的本次命中不豁免同章其他文本()
    {
        var book = Add(Story("不可思议的城市。不可思议的巧合。"), "不可思议", exceptions: "不可思议的城市");
        var finding = Assert.Single(RuleEvaluation.Check(book, book.Chapters[0].Id, null).Findings);
        Assert.Equal("不可思议的城市。".Length, finding.Start);
        var partial = Add(Story("不可思议"), "不可思议", exceptions: "思议");
        Assert.Single(RuleEvaluation.Check(partial, partial.Chapters[0].Id, null).Findings);
    }
    [Fact]
    public void 必须包含与禁止硬规则的确定冲突可见且建议不冒充硬失败()
    {
        var book = Add(Add(Story("没有目标词"), "归途", WritingRuleKind.RequiredText), "归途");
        var result = RuleEvaluation.Check(book, book.Chapters[0].Id, null);
        Assert.True(result.HasHardFailure); Assert.Single(result.Conflicts); Assert.Equal(-1, Assert.Single(result.Findings).Start);
        var advisory = Add(Story("归途"), "归途", strength: WritingRuleStrength.Advisory);
        var checkedAdvisory = RuleEvaluation.Check(advisory, advisory.Chapters[0].Id, null);
        Assert.False(checkedAdvisory.HasHardFailure); Assert.Single(checkedAdvisory.Findings);
    }
    [Fact]
    public void 语义指导和旧规则原文均明确保持待检查()
    {
        var book = Add(Story("平静的夜晚"), "", WritingRuleKind.Guidance) with { Profile = WritingProfile.Empty with { Rules = "不要让角色行为失去动机" } };
        var check = RuleEvaluation.Check(book, book.Chapters[0].Id, null);
        Assert.Equal(2, check.GuidanceCount); Assert.Empty(check.Findings);
    }
    [Fact]
    public void 本卷规则不泄漏到其他卷且旧卷规则编辑保持原目标()
    {
        var book = Add(Story("离别"), "离别", scope: WritingRuleScope.Volume);
        book = BookEdits.AddVolume(book).Project;
        var first = book.Chapters[0]; var second = book.Chapters[1];
        Assert.Single(RuleEvaluation.Applicable(book, first.Id, null)); Assert.Empty(RuleEvaluation.Applicable(book, second.Id, null));
        book = WritingRuleSet.Commit(book, book.RuleEditor with { Interpretation = "在另一卷查看时补充说明" }, second.Id);
        Assert.Equal(first.VolumeId, Assert.Single(book.Rules.Items).ScopeId);
        var copied = book.CopyAsNew(); copied.Validate();
        Assert.Equal(copied.Chapters[0].VolumeId, Assert.Single(copied.Rules.Items).ScopeId);
    }
    [Fact]
    public void 本次运行规则只匹配指定运行且结束后可以停用()
    {
        var book = Story("已经离开港口"); var chapter = book.Chapters[0]; var run = Guid.NewGuid();
        var submission = new DraftSubmission(book.Id, chapter.Id, null, null, RevisionRules.Hash(chapter.Text), RevisionRules.ContextStamp(book, chapter.Id), chapter.Text, "", [], RevisionCheck.NotChecked, run, Guid.NewGuid());
        book = book with { Revisions = RevisionRules.CommitWorking(book, submission) };
        book = Add(book, "港口", scope: WritingRuleScope.Run);
        Assert.Single(RuleEvaluation.Applicable(book, chapter.Id, run)); Assert.Empty(RuleEvaluation.Applicable(book, chapter.Id, Guid.NewGuid()));
        book = book with { Revisions = RevisionRules.DiscardWorking(book) };
        book = WritingRuleSet.Commit(book, book.RuleEditor with { Enabled = false }, chapter.Id);
        Assert.False(Assert.Single(book.Rules.Items).Enabled);
    }
    [Fact]
    public void 正文规则启停和旧原文变化均使检查失效()
    {
        var book = Add(Story("故事在继续"), "总而言之"); var chapter = book.Chapters[0];
        var check = RuleEvaluation.Check(book, chapter.Id, null); Assert.True(RuleEvaluation.IsCurrent(book, check));
        Assert.False(RuleEvaluation.IsCurrent(book with { Chapters = book.Chapters.SetItem(0, chapter with { Text = "新正文" }) }, check));
        Assert.False(RuleEvaluation.IsCurrent(book with { Profile = book.Profile with { Rules = "新的原文" } }, check));
        var disabled = WritingRuleSet.Commit(book, book.RuleEditor with { Enabled = false }, chapter.Id);
        Assert.False(RuleEvaluation.IsCurrent(disabled, check)); Assert.Empty(RuleEvaluation.Applicable(disabled, chapter.Id, null));
    }
    [Fact]
    public void 空匹配或不支持的例外不能成为已保存规则()
    {
        var book = Story("任意正文");
        Assert.Throws<InvalidDataException>(() => Add(book, "， 。", ignore: true));
        Assert.Throws<InvalidDataException>(() => Add(book, "甲", WritingRuleKind.RequiredText, exceptions: "例外"));
        Assert.Empty(book.Rules.Items);
    }
    [Fact]
    public async Task 规则草案版本检测与过期提示可保存重开()
    {
        await using var workspace = new TestWorkspace();
        await using (var document = workspace.CreateDocument())
        {
            await document.InitializeAsync(new NewDocumentActivation("规则测试"), CancellationToken.None);
            workspace.Interaction.NextPath = workspace.ProjectPath(); await document.NewProjectCommand.ExecuteAsync(null);
            document.ChapterText = "开头。总而言之，主角回家了。";
            document.RuleOriginal = "禁用总结式结尾"; document.RulePattern = "总而言之";
            await document.SaveRuleVersionCommand.ExecuteAsync(null); await document.CheckLocalRulesCommand.ExecuteAsync(null);
            document.SelectedFinding = Assert.Single(document.RuleFindings); document.LocateRuleFindingCommand.Execute(null);
            Assert.Equal(3, document.EditorSelectionStart); Assert.Equal(7, document.EditorSelectionEnd);
            Assert.Contains("硬规则问题", document.LocalRuleStatus); Assert.Contains("总而言之", document.ChapterText);
            document.RuleEnabled = false; await document.SaveRuleVersionCommand.ExecuteAsync(null); Assert.Contains("过期", document.LocalRuleStatus);
            document.NewRuleCommand.Execute(null); document.RuleOriginal = "尚未整理完的草案";
            await document.SaveCommand.ExecuteAsync(null);
        }
        await using var reopened = workspace.CreateDocument(); await reopened.InitializeAsync(new NewDocumentActivation("重开"), CancellationToken.None);
        workspace.Interaction.NextPath = workspace.ProjectPath(); await reopened.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal("尚未整理完的草案", reopened.RuleOriginal); Assert.Single(reopened.WritingRules);
        Assert.Contains("过期", reopened.LocalRuleStatus);
    }
}
