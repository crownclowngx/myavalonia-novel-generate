namespace NovelGeneratePlugin.Infrastructure.Persistence;

public sealed record WorkspacePaths(string Root)
{
    public static WorkspacePaths ForCurrentUser() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovelGeneratePlugin"));
    public string Catalog => Path.Combine(Root, "catalog.db");
    public string RecoveryDirectory => Path.Combine(Root, "Recovery");
}
