using System.Text;
using NovelGeneratePlugin.Application.Analysis;

namespace NovelGeneratePlugin.Infrastructure.Export;

/// <summary>先在同目录提交完整临时文件再原子改名，只接受新 Markdown 路径；取消或失败不能留下半份报告。</summary>
public sealed class AnalysisReportWriter : IAnalysisReportWriter
{
    public Task WriteAsync(NovelAnalysisReport report, string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var destination = Path.GetFullPath(path);
        if (!destination.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择 .md 报告文件。");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标已存在，请选择新文件名。");
        var text = NovelReportMarkdown.Format(report); cancellationToken.ThrowIfCancellationRequested();
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(new UTF8Encoding(false).GetBytes(text)); stream.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellationToken);
}
