param([Parameter(Mandatory = $true)][string]$PluginDirectory)
$ErrorActionPreference = 'Stop'
$novelPluginPath = (Resolve-Path -LiteralPath $PluginDirectory).Path
$novelProbePath = Join-Path ([IO.Path]::GetTempPath()) ('NovelStageProbe-' + [Guid]::NewGuid().ToString('N') + '.noveldb')

# 探针从干净的开发暂存目录加载私有托管与原生依赖，不借用测试 bin 中的 SQLite。
# 只验证存储资产，不生成 ZIP、不触碰真实 Host，也不代替宿主加载验收。
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
public sealed class NovelStorageProbe : AssemblyLoadContext
{
    private readonly string root;
    public NovelStorageProbe(string root) : base(isCollectible: true) { this.root = root; }
    protected override Assembly Load(AssemblyName name)
    {
        var path = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(path)) return LoadFromAssemblyPath(path);
        return null;
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = Path.Combine(root, "runtimes", "win-x64", "native", "e_sqlite3.dll");
        return name.Contains("sqlite3") ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
    public static void Verify(string root, string path)
    {
        var context = new NovelStorageProbe(root);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(root, "NovelGeneratePlugin.Plugin.dll"));
            var projectType = assembly.GetType("NovelGeneratePlugin.Domain.BookProject", true);
            var storeType = assembly.GetType("NovelGeneratePlugin.Infrastructure.Persistence.ProjectStore", true);
            var project = projectType.GetMethod("Create").Invoke(null, new object[] { "暂存资产验证", "独立 ALC 与原生 SQLite" });
            var store = Activator.CreateInstance(storeType);
            storeType.GetMethod("Create").Invoke(store, new object[] { path, project });
            var stored = storeType.GetMethod("Read").Invoke(store, new object[] { path });
            var read = stored.GetType().GetProperty("Project").GetValue(stored);
            if (!Equals(projectType.GetProperty("Idea").GetValue(read), "独立 ALC 与原生 SQLite"))
                throw new InvalidDataException("暂存资产数据库往返内容不一致");
        }
        finally { context.Unload(); }
    }
}
'@
try {
    [NovelStorageProbe]::Verify($novelPluginPath, $novelProbePath)
    Write-Output '开发暂存 SQLite 探针通过：独立 ALC、win-x64 原生库、中文项目创建/读取。'
} finally {
    # 仅删除本次生成的三个明确文件，不使用通配符或递归清理临时目录。
    foreach ($novelProbeFile in @($novelProbePath, "$novelProbePath-wal", "$novelProbePath-shm")) {
        if (Test-Path -LiteralPath $novelProbeFile) { Remove-Item -LiteralPath $novelProbeFile }
    }
}
