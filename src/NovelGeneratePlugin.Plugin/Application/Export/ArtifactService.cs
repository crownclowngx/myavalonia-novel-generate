using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Export;

public interface IArtifactFiles
{
    void WriteManuscript(string path, string content, ManuscriptFormat format);
    void CopyProject(string sourcePath, string destinationPath);
    TemplateAsset ReadTemplate(string path);
    void WriteTemplate(string path, TemplateAsset template);
}
/// <summary>导出用例只协调领域快照与文件端口；不读取凭据目录，也不复制整份用户目录。</summary>
public sealed class ArtifactService(IArtifactFiles files, TemplateLibrary templates)
{
    public Task<PreparedManuscript> PrepareAsync(BookProject book, ExportSelection selection) => Task.Run(() => ManuscriptExport.Prepare(book, selection));
    public Task WriteAsync(PreparedManuscript prepared, string path) => Task.Run(() => files.WriteManuscript(path, prepared.Content, prepared.Selection.Format));
    public Task BackupAsync(string sourcePath, string destinationPath) => Task.Run(() => files.CopyProject(sourcePath, destinationPath));
    public async Task ExportTemplateAsync(Guid id, string path)
    { var asset = await templates.ReadAsync(id).ConfigureAwait(false); await Task.Run(() => files.WriteTemplate(path, asset)).ConfigureAwait(false); }
    public async Task<TemplateAsset> ImportTemplateAsync(string path)
    { var asset = await Task.Run(() => files.ReadTemplate(path)).ConfigureAwait(false); return await templates.ImportCopyAsync(asset).ConfigureAwait(false); }
}
