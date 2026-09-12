using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

/// <summary>以完整章节为串行单位，作品事务先于检查点推进；任何必要检查失败都停在当前章，不跨过因果依赖。</summary>
public sealed class ContinuousRunService(PlanningService planning, ChapterGenerationService generation, ModelRequestService requests,
    IChapterWorkStore workStore, IContinuousRunStore store)
{
    public IReadOnlyList<ModelRequestEntry> Usage(ContinuousRun run) => requests.List(run.Budget.Id);
    public Task<ContinuousRun?> LoadAsync(Guid bookId) => Task.Run(() =>
    {
        var run = store.Latest(bookId);
        if (run is null || run.State is not (ContinuousRunState.Running or ContinuousRunState.Queued or ContinuousRunState.Committing)) return run;
        IDisposable lease;
        try { lease = store.Acquire(bookId); } catch (InvalidOperationException) { return run; }
        using (lease)
        {
            run = store.Get(run.Id)!;
            if (run.State is ContinuousRunState.Running or ContinuousRunState.Queued or ContinuousRunState.Committing)
            {
                // 能独占运行锁说明没有仍在执行的所有者；只标记中断，不调用模型、不推进章节。
                run = run with { State = ContinuousRunState.Interrupted, Message = "发现中断检查点，请核对用量后明确继续。", Sequence = run.Sequence + 1 }; store.Save(run);
            }
            return run;
        }
    });
    public async Task<ContinuousRun> StartAsync(ProjectSession session, Guid startChapterId, RunPolicy policy, RunControl control,
        IProgress<ContinuousRun>? progress, CancellationToken cancellationToken)
    {
        policy.Validate(); cancellationToken.ThrowIfCancellationRequested(); using var lease = store.Acquire(session.Id);
        var saved = await session.SaveAsync().ConfigureAwait(false); if (!saved.Saved) throw new IOException("连续运行要求作品原库保存成功，不能只依赖恢复副本。");
        var book = session.Current;
        var run = new ContinuousRun(Guid.NewGuid(), book.Id, startChapterId, book.Revisions.ActiveRunId ?? Guid.NewGuid(), policy, new(Guid.NewGuid(), policy.MaximumRequests, policy.MaximumTokens),
            book, [], 0, null, [], ContinuousRunState.Queued, "运行已排队。", 1);
        store.Create(run); return await ExecuteAsync(session, run, control, progress, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ContinuousRun> ResumeAsync(ProjectSession session, Guid runId, int maximumRequests, long maximumTokens, bool acknowledgeUncertain,
        RunControl control, IProgress<ContinuousRun>? progress, CancellationToken cancellationToken)
    {
        using var lease = store.Acquire(session.Id); var run = store.Get(runId) ?? throw new InvalidOperationException("运行记录不存在。");
        if (run.BookId != session.Id || run.State == ContinuousRunState.Completed) throw new InvalidOperationException("运行不属于本书或已经完成。");
        var active = store.Latest(session.Id);
        if (active is not null && active.Id != run.Id && active.State is not (ContinuousRunState.Completed or ContinuousRunState.Cancelled))
            throw new InvalidOperationException("本书已有另一个未结束运行，不能恢复旧运行覆盖它。");
        cancellationToken.ThrowIfCancellationRequested();
        var policy = run.Policy with { MaximumRequests = maximumRequests, MaximumTokens = maximumTokens }; policy.Validate();
        var budget = run.Budget with { MaximumRequests = maximumRequests, MaximumTokens = maximumTokens }; requests.ReviseBudget(budget, acknowledgeUncertain);
        run = run with { Policy = policy, Budget = budget };
        return await ExecuteAsync(session, run, control, progress, cancellationToken).ConfigureAwait(false);
    }
    public ContinuousRun Abandon(Guid bookId, Guid runId)
    {
        using var lease = store.Acquire(bookId); var run = store.Get(runId) ?? throw new InvalidOperationException("运行记录不存在。");
        if (run.BookId != bookId) throw new InvalidOperationException("运行不属于本书。");
        run = run with { State = ContinuousRunState.Cancelled, Message = "作者结束运行，已有工作稿与候选保留。", Sequence = run.Sequence + 1 }; store.Save(run); return run;
    }
    private async Task<ContinuousRun> ExecuteAsync(ProjectSession session, ContinuousRun initial, RunControl control, IProgress<ContinuousRun>? progress, CancellationToken ct)
    {
        var run = initial;
        void Save(ContinuousRunState state, string message)
        {
            var next = run with { State = state, Message = message, Sequence = run.Sequence + 1 }; store.Save(next); run = next;
            try { progress?.Report(run); } catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        void MatchCheckpoint()
        {
            if (CanonicalJson.Hash(session.Current) != CanonicalJson.Hash(run.Checkpoint)) throw new InvalidOperationException("作品在检查点后有修改，请保留旧运行并重新核对规划与规范。");
        }
        try
        {
            if (run.PreparedPlanning is { } recoveredPlanning)
            {
                if (CanonicalJson.Hash(session.Current) == CanonicalJson.Hash(run.Checkpoint))
                {
                    ct.ThrowIfCancellationRequested(); session.Update(recoveredPlanning);
                    var saved = await session.SaveAsync().ConfigureAwait(false); if (!saved.Saved) throw new IOException("待恢复规划尚未保存到作品原库。");
                }
                if (CanonicalJson.Hash(session.Current) != CanonicalJson.Hash(recoveredPlanning)) throw new InvalidOperationException("规划检查点之外还有作者修改，不能自动恢复覆盖。");
                run = run with { Checkpoint = session.Current, PreparedPlanning = null }; Save(ContinuousRunState.Running, "已核对规划保存检查点，未重复生成规划。");
            }
            // 中断可能发生在作品已提交、检查点未前移之间。只接受严格匹配的单条候选提交差异。
            if (run.PendingWorkId is Guid pending && session.Current.Revisions.History.Any(r => r.OperationId == pending))
            {
                var work = workStore.Get(pending) ?? throw new InvalidDataException("提交对应的候选记录缺失。");
                VerifyCommittedCheckpoint(run.Checkpoint, session.Current, work);
                run = run with { Checkpoint = session.Current, NextChapter = run.NextChapter + 1, PendingWorkId = null, CommittedOperations = run.CommittedOperations.Add(pending) };
                Save(ContinuousRunState.Running, "已核对中断前完成的提交，没有重复调用模型。");
            }
            MatchCheckpoint(); ct.ThrowIfCancellationRequested();
            if (run.Chapters.IsEmpty)
            {
                var book = session.Current; var targets = book.Chapters.SkipWhile(c => c.Id != run.StartChapterId).Take(run.Policy.ChapterCount).ToArray();
                var ready = targets.Length == run.Policy.ChapterCount;
                foreach (var chapter in targets) try { PlanningRules.RequireReady(book, chapter.Id); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { ready = false; }
                if (!ready)
                {
                    if (!run.Policy.PlanIfMissing) throw new InvalidOperationException("目标范围缺少有效章纲，请先规划或选择自动补齐规划。");
                    Save(ContinuousRunState.Running, "正在补齐近期规划，计入同一运行预算。");
                    var candidate = await planning.GenerateAsync(book, run.StartChapterId, run.Policy.ChapterCount, run.Budget, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested(); MatchCheckpoint();
                    var prepared = PlanningRules.Apply(session.Current, candidate);
                    // 随机卷章/实体/规划修订身份只生成一次；先记录完整待写快照，再进入作品保存。
                    run = run with { PreparedPlanning = prepared }; Save(ContinuousRunState.Running, "规划已生成，正在保存规划检查点。");
                    ct.ThrowIfCancellationRequested(); session.Update(prepared);
                    var saved = await session.SaveAsync().ConfigureAwait(false); if (!saved.Saved) throw new IOException("规划尚未写入作品原库，运行停止。");
                    if (CanonicalJson.Hash(session.Current) != CanonicalJson.Hash(prepared)) throw new InvalidOperationException("规划保存期间作者又修改了作品，请复核检查点。");
                    run = run with { Checkpoint = session.Current, PreparedPlanning = null };
                }
                run = run with { Chapters = session.Current.Chapters.SkipWhile(c => c.Id != run.StartChapterId).Take(run.Policy.ChapterCount).Select(c => c.Id).ToImmutableArray() };
                Save(ContinuousRunState.Running, "近期规划已就绪。");
            }
            while (run.NextChapter < run.Chapters.Length)
            {
                ct.ThrowIfCancellationRequested(); MatchCheckpoint();
                if (control.PauseRequested) { Save(ContinuousRunState.Paused, "已在章节检查点暂停，没有发出下一章请求。"); return run; }
                var chapterId = run.Chapters[run.NextChapter]; ChapterWork? work = run.PendingWorkId is Guid id ? workStore.Get(id) : null;
                if (work?.State != ChapterWorkState.Ready)
                {
                    Save(ContinuousRunState.Running, $"正在生成并审校第 {run.NextChapter + 1}/{run.Chapters.Length} 章。");
                    work = await generation.GenerateAsync(session.Current, chapterId, run.StoryRunId, run.Policy.TargetCharacters, run.Policy.MaximumRepairs, run.Budget, null, ct).ConfigureAwait(false);
                    run = run with { PendingWorkId = work.Id };
                }
                ct.ThrowIfCancellationRequested(); MatchCheckpoint();
                if (work.State != ChapterWorkState.Ready) { Save(ContinuousRunState.NeedsAttention, work.Message); return run; }
                Save(ContinuousRunState.Committing, "本章检查通过，正在原子提交工作稿。");
                await session.CommitGeneratedChapterAsync(work, ct).ConfigureAwait(false);
                // 已进入本地提交后即便取消，也先记录完成的操作，再响应停止。
                VerifyCommittedCheckpoint(run.Checkpoint, session.Current, work);
                run = run with { Checkpoint = session.Current, NextChapter = run.NextChapter + 1, PendingWorkId = null, CommittedOperations = run.CommittedOperations.Add(work.Id) };
                Save(ContinuousRunState.Running, $"已保存 {run.NextChapter}/{run.Chapters.Length} 章工作稿。");
            }
            Save(ContinuousRunState.Completed, "目标章节已全部保存为工作稿，等待作者复核定稿。"); return run;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Save(ct.IsCancellationRequested ? ContinuousRunState.Cancelled : ContinuousRunState.NeedsAttention,
                ct.IsCancellationRequested ? "已取消，已提交工作稿与已有候选保留。" : error.Message);
            return run;
        }
    }
    public static void VerifyCommittedCheckpoint(BookProject before, BookProject current, ChapterWork work)
    {
        var expected = ChapterGenerationRules.Commit(before, work); var actual = current.Revisions.History.SingleOrDefault(r => r.OperationId == work.Id) ?? throw new InvalidOperationException("提交结果不存在。");
        var generated = expected.Revisions.History.Last();
        // 新修订的随机 Id/时间是实际提交时生成的，比较时仅用它们替换预演值，其余内容与指针必须一致。
        expected = expected with
        {
            Revisions = expected.Revisions with
            {
                History = expected.Revisions.History.SetItem(expected.Revisions.History.Length - 1, generated with { Id = actual.Id, CreatedAt = actual.CreatedAt }),
                Heads = expected.Revisions.Heads.Select(h => h.WorkingId == generated.Id ? h with { WorkingId = actual.Id } : h).ToImmutableArray()
            }
        };
        if (CanonicalJson.Hash(expected) != CanonicalJson.Hash(current)) throw new InvalidOperationException("中断后的作品不只是预期提交变化，不能自动推进检查点。");
    }
}
