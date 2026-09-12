namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>小说报告的窄文件端口，界面不持有文件流，也不需要整个创作项目导出接口。</summary>
public interface IAnalysisReportWriter
{
    Task WriteAsync(NovelAnalysisReport report, string path, CancellationToken cancellationToken);
}
