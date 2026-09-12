// 显式收费的三章实测入口，不由单元门禁调用。只使用既有 Codex 登录，不读取或复制认证文件。
// 章纲是固定的人工验收输入；模型真正生成正文、摘要与事实，预算和落盘仍走生产服务。
using System.Collections.Immutable;
using System.Text.Json;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Models;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Infrastructure.Credentials;
if (args.Length != 3 || args[2] != "--run-paid-sample") throw new ArgumentException("需提供 Codex exe、空输出目录和 --run-paid-sample；此工具会消耗套餐额度。");
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("样稿输出目录必须为空，不能覆盖已有验收记录。");
Directory.CreateDirectory(root); var paths = new WorkspacePaths(Path.Combine(root, "probe-data"));
using var vault = new UserCredentialVault(paths); var connections = new ConnectionService(new ConnectionStore(paths), vault);
var preset = new ModelPreset("gpt-6-astra", 8192, "low");
var connection = await connections.SaveAsync(null, new("G0016 套餐三章实测", ModelProvider.CodexCli, "", args[0], preset, preset, preset));
var book = BookProject.Create("雨夜未寄出的信", "林舟回到废弃旧邮局，找到自己十年前留下的信，并写下回信。现实故事，只有一个出场人物。");
book = book with { Profile = new("现实世界，仅林舟出场，无超自然事件，不引入其他有名字的人物。旧邮局是地点，旧信和铜钥匙是物品。", "有限第三人称，语言朴素，靠动作和感官细节推进，不堆砌形容词。", "先建立期待，再制造阻碍，最后兑现。", "不出现其他人物的内心独白。"), Connection = ConnectionService.Bind(connection) };
var chapters = new[]{
 new ProposedChapter(1,"雨中的旧门","进入旧邮局寻找十年前留下的旧信","门锁生锈，铜钥匙难以转动","林舟有限第三人称","同一雨夜，旧邮局门前","林舟用随身铜钥匙打开生锈门锁，进入旧邮局。结尾仅发现柜台抽屉，不提前找到或读信。","林舟从门外进入旧邮局","柜台抽屉上熟悉的刻痕","下一章打开抽屉寻找旧信",[new MethodApplication("先建立期待，再制造阻碍，最后兑现。","十年前留下的信可能仍在","生锈门锁打不开","门终于打开，发现柜台抽屉")]),
 new ProposedChapter(1,"抽屉里的年月","找到并读完旧信","抽屉受潮卡住，信纸脆弱","林舟有限第三人称","同一雨夜，旧邮局柜台","林舟打开抽屉，拿到旧信并读完。旧信是十年前他写给未来自己的信，内容是希望未来的自己仍愿意写字。","林舟读懂自己十年前的愿望，旧信在林舟手中","空白信纸可以写回信","下一章回应十年前的自己",[new MethodApplication("先建立期待，再制造阻碍，最后兑现。","想知道旧信写了什么","潮湿抽屉与脆弱信纸","读出自己十年前的愿望")]),
 new ProposedChapter(1,"回信","写下给十年前自己的回应","林舟不知如何面对未能实现的愿望","林舟有限第三人称","同一雨夜，旧邮局柜台","林舟用柜台上的空白信纸写回信，承认遗憾但决定重新写字；把回信和旧信装进随身邮袋，等待雨停。故事在此收束，不引入新的谜团或人物。","林舟完成回信并把两封信放入自己的邮袋","旧信与回信并存","结尾收束",[new MethodApplication("先建立期待，再制造阻碍，最后兑现。","希望回应过去的自己","难以落笔面对遗憾","完成回信，决定重新写字")])
}.ToImmutableArray();
var entities = new[] { new ProposedEntity("林舟", StoryEntityKind.Person, [], "唯一出场人物，成年邮差"), new ProposedEntity("旧邮局", StoryEntityKind.Place, [], "废弃邮局"), new ProposedEntity("铜钥匙", StoryEntityKind.Item, [], "林舟随身携带的旧邮局钥匙"), new ProposedEntity("旧信", StoryEntityKind.Item, [], "林舟十年前写给未来自己的信"), new ProposedEntity("回信", StoryEntityKind.Item, [], "林舟将在第三章写的回信，开场尚未完成"), new ProposedEntity("邮袋", StoryEntityKind.Item, [], "林舟随身的邮袋") }.ToImmutableArray();
var proposal = new PlanningProposal("一个雨夜，林舟回到旧邮局，找到十年前给自己的信，完成回应后重新开始写字。", [new("雨夜来信", "从寻找旧信到完成回信，收束林舟的内心选择")], chapters, entities);
book = PlanningRules.Apply(book, new(book.Id, book.Chapters[0].Id, PlanningRules.SourceStamp(book), Guid.NewGuid(), "G0016 明确三章验收脚本（人工编排，不冒充模型规划）", proposal));
var projectStore = new ProjectStore(); await using var sessions = new ProjectSessions(projectStore, new CatalogStore(paths), new RecoveryStore(paths), new FileProjectLeaseProvider());
await using var session = await sessions.CreateAsync(Path.Combine(root, "雨夜未寄出的信.noveldb"), book);
using var client = DeepSeekTextModel.CreateClient(); var ledger = new ModelRequestStore(paths); var works = new ChapterWorkStore(paths);
var requests = new ModelRequestService(new TextModelRouter(connections, new DeepSeekTextModel(connections, client), new CodexTextModel(new CodexProcess())), ledger);
var runner = new ContinuousRunService(new PlanningService(connections, requests), new ChapterGenerationService(connections, requests, works), requests, works, new ContinuousRunStore(paths));
var json = new JsonSerializerOptions { WriteIndented = true }; File.WriteAllText(Path.Combine(root, "input.json"), JsonSerializer.Serialize(book, json));
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
// 一次运行有明确的总预算和最长时间，检查失败即停，不用脚本绕过领域门禁自动重试。
var run = await runner.StartAsync(session, book.Chapters[0].Id, new(3, 500, 1, 12, 400000, false), new RunControl(), new RunProgress(), stop.Token);
File.WriteAllText(Path.Combine(root, "run.json"), JsonSerializer.Serialize(run, json)); var usage = runner.Usage(run); File.WriteAllText(Path.Combine(root, "requests.json"), JsonSerializer.Serialize(usage, json));
var stored = projectStore.Read(session.Path).Project; File.WriteAllText(Path.Combine(root, "book.json"), JsonSerializer.Serialize(stored, json));
if (run.NextChapter > 0) { var export = ManuscriptExport.Prepare(stored, new(ManuscriptVersion.WorkingView, ManuscriptFormat.Markdown, 1, run.NextChapter)); File.WriteAllText(Path.Combine(root, "三章工作稿.md"), export.Content); }
Console.WriteLine($"RESULT {run.State} {run.NextChapter}/3; requests={usage.Count}; charged={usage.Sum(e => e.ChargedTokens)}; unknown={usage.Count(e => e.Usage.InputTokens is null || e.Usage.OutputTokens is null)}; formal={stored.Revisions.Heads.Count(h => h.FormalId is not null)}");
if (run.State != ContinuousRunState.Completed) Environment.ExitCode = 2;
sealed class RunProgress : IProgress<ContinuousRun> { private string? last; public void Report(ContinuousRun run) { var text = $"STATE {run.State} {run.NextChapter}/3 {run.Message}"; if (text != last) Console.WriteLine(text); last = text; } }
