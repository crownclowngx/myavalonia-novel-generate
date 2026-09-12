using System.Collections.Immutable;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

public enum ContinuousRunState { Queued, Running, Paused, NeedsAttention, Committing, Completed, Cancelled, Failed, Interrupted }
public sealed record RunPolicy(int ChapterCount, int TargetCharacters, int MaximumRepairs, int MaximumRequests, long MaximumTokens, bool PlanIfMissing)
{
    public void Validate()
    {
        if (ChapterCount is < 3 or > 5 || TargetCharacters is < 100 or > 10000 || MaximumRepairs is < 0 or > 2 || MaximumRequests is < 1 or > 1000 || MaximumTokens is < 1 or > 10000000)
            throw new InvalidDataException("连续运行的章数、字数、修正次数或预算无效。");
    }
}
/// <summary>检查点保留提交前完整作品快照，用于识别磁盘已提交而运行记录尚未前移的中断，不猜测成功。</summary>
public sealed record ContinuousRun(Guid Id, Guid BookId, Guid StartChapterId, Guid StoryRunId, RunPolicy Policy, RequestBudget Budget,
    BookProject Checkpoint, ImmutableArray<Guid> Chapters, int NextChapter, Guid? PendingWorkId, ImmutableArray<Guid> CommittedOperations,
    ContinuousRunState State, string Message, long Sequence)
{
    public BookProject? PreparedPlanning { get; init; }
    public void Validate()
    {
        Policy.Validate(); Checkpoint.Validate(); PreparedPlanning?.Validate();
        if (PreparedPlanning is not null && PreparedPlanning.Id != BookId) throw new InvalidDataException("规划检查点不属于本书。");
        if (Id == Guid.Empty || BookId != Checkpoint.Id || StartChapterId == Guid.Empty || StoryRunId == Guid.Empty || Budget.Id == Guid.Empty ||
            Budget.MaximumRequests != Policy.MaximumRequests || Budget.MaximumTokens != Policy.MaximumTokens ||
            Chapters.IsDefault || (!Chapters.IsEmpty && Chapters.Length != Policy.ChapterCount) || Chapters.Any(id => !Checkpoint.Chapters.Any(c => c.Id == id)) || Chapters.Length > 5 || Chapters.Distinct().Count() != Chapters.Length || NextChapter < 0 || NextChapter > Chapters.Length ||
            CommittedOperations.IsDefault || CommittedOperations.Length != NextChapter || CommittedOperations.Distinct().Count() != CommittedOperations.Length || !Enum.IsDefined(State) || Message is null || Sequence < 1)
            throw new InvalidDataException("连续运行检查点无效。");
    }
}
public interface IContinuousRunStore
{
    IDisposable Acquire(Guid bookId);
    void Create(ContinuousRun run);
    void Save(ContinuousRun run);
    ContinuousRun? Get(Guid id);
    ContinuousRun? Latest(Guid bookId);
}
/// <summary>暂停只在本章完整检查和提交后的检查点生效；取消立即传递给网络，已进入的本地事务完成后再停止。</summary>
public sealed class RunControl
{
    private int _pause;
    public bool PauseRequested => Volatile.Read(ref _pause) != 0;
    public void RequestPause() => Interlocked.Exchange(ref _pause, 1);
}
