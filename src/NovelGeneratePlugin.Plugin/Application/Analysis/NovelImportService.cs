using System.Collections.Immutable;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record NovelImportPreview(ReferenceImport Import, PartitionOptions Options, ImmutableArray<string> Warnings);

/// <summary>
/// 两步导入：预览只读文件、解码和分章；确认后原子保存这份不可变快照，不在确认时悄悄重读已经变化的路径。
/// 取消在进入保存事务前生效，事务开始后由存储完成整笔提交，避免“用户取消”留下半本来源。
/// </summary>
public sealed class NovelImportService(ITextSourceReader reader, IReferenceSourceStore store)
{
    public async Task<NovelImportPreview> PreviewAsync(string path, string? encoding, PartitionOptions options, CancellationToken ct)
    {
        options.Validate();
        var content = await reader.ReadAsync(path, encoding, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var source = SourceSnapshot.Create(Guid.NewGuid(), content.FileName, content.EncodingName, content.Bytes, content.Text);
            var import = NovelTextPartitioner.Partition(source, Path.GetFileNameWithoutExtension(content.FileName), options, ct);
            var warnings = content.Warnings;
            if (import.Sections.Any(s => s.Title.Contains("未识别章标题", StringComparison.Ordinal))) warnings = warnings.Add("未识别可靠章标题，已按连续片段覆盖全文。");
            return new NovelImportPreview(import, options, warnings);
        }, ct).ConfigureAwait(false);
    }

    public Task<ReferenceBook> ImportAsync(NovelImportPreview preview, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); preview.Import.Validate();
        return store.Import(preview.Import);
    }, ct);
}
