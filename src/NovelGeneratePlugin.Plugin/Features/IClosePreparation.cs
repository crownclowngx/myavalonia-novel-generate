namespace NovelGeneratePlugin.Features;
/// <summary>开发承载窗口在释放 Scope 前排空编辑；不替代或扩展公开 Host 关闭契约。</summary>
public interface IClosePreparation { Task<bool> SaveBeforeCloseAsync(); }
