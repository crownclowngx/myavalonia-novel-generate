using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>
/// 来源资产的窄持久化端口：领域不认识 SQLite，调用者也不持有连接或事务。
/// 一次 Import 同时提交书目、不可变来源及分章清单；列表只读小型元数据，正文按书读取。
/// </summary>
public interface IReferenceSourceStore
{
    ReferenceBook Import(ReferenceImport source);
    ReferenceBook ReadBook(Guid id);
    SourceSnapshot ReadSource(Guid bookId);
    ReferenceImport Read(Guid bookId);
    IReadOnlyList<ReferenceBook> List(int offset = 0, int limit = 50);
    ReferenceBook Rename(Guid id, long expectedVersion, string name);
}
