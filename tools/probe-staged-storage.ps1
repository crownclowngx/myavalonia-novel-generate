param([Parameter(Mandatory = $true)][string]$PluginDirectory)
$ErrorActionPreference = 'Stop'
$novelPluginPath = (Resolve-Path -LiteralPath $PluginDirectory).Path
$novelProbePath = Join-Path ([IO.Path]::GetTempPath()) ('NovelStageProbe-' + [Guid]::NewGuid().ToString('N') + '.noveldb')

# 探针从干净的开发暂存目录加载私有托管与原生依赖，不借用测试 bin 中的 SQLite。
# 验证存储与中文检索资产，不生成 ZIP、不触碰真实 Host，也不代替宿主加载验收。
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            // 使用真实聚合和修订规则建立中文夹具，再在独立加载上下文调用检索；不引用测试程序集。
            var node = JsonNode.Parse(JsonSerializer.Serialize(project, projectType));
            var chapters = node["Chapters"].AsArray(); var firstId = Guid.Parse(chapters[0]["Id"].GetValue<string>());
            var second = chapters[0].DeepClone(); var secondId = Guid.NewGuid(); second["Id"] = secondId.ToString(); second["Title"] = "第二章";
            chapters[0]["Text"] = "林舟在雾港打开书店。"; chapters[0]["Summary"] = "林舟抵达书店"; chapters.Add(second);
            node["Story"]["Entities"] = JsonNode.Parse("[{\"Id\":\"" + Guid.NewGuid() + "\",\"Kind\":0,\"Name\":\"林舟\",\"Aliases\":[\"阿舟\"],\"Description\":\"邮差\",\"LockedFields\":{}}]");
            project = JsonSerializer.Deserialize(node.ToJsonString(), projectType);
            var rules = assembly.GetType("NovelGeneratePlugin.Domain.RevisionRules", true);
            var hash = rules.GetMethod("Hash").Invoke(null, new object[] { "林舟在雾港打开书店。" });
            var stamp = rules.GetMethod("ContextStamp").Invoke(null, new object[] { project, firstId, true });
            var factArray = typeof(ImmutableArray<>).MakeGenericType(assembly.GetType("NovelGeneratePlugin.Domain.FactDelta", true)).GetField("Empty").GetValue(null);
            var submission = Activator.CreateInstance(assembly.GetType("NovelGeneratePlugin.Domain.DraftSubmission", true), new object[] {
                Guid.Parse(node["Id"].GetValue<string>()), firstId, null, null, hash, stamp, "林舟在雾港打开书店。", "林舟抵达书店", factArray,
                Enum.ToObject(assembly.GetType("NovelGeneratePlugin.Domain.RevisionCheck", true), 0), Guid.NewGuid(), Guid.NewGuid() });
            var ledger = rules.GetMethod("CommitWorking").Invoke(null, new object[] { project, submission }); projectType.GetProperty("Revisions").SetValue(project, ledger);
            var store = Activator.CreateInstance(storeType);
            storeType.GetMethod("Create").Invoke(store, new object[] { path, project });
            var stored = storeType.GetMethod("Read").Invoke(store, new object[] { path });
            var read = stored.GetType().GetProperty("Project").GetValue(stored);
            if (!Equals(projectType.GetProperty("Idea").GetValue(read), "独立 ALC 与原生 SQLite"))
                throw new InvalidDataException("暂存资产数据库往返内容不一致");
            var search = assembly.GetType("NovelGeneratePlugin.Domain.StoryMemory", true).GetMethod("Search");
            foreach (var query in new[] { "林舟", "阿舟" })
            {
                var matches = (IEnumerable)search.Invoke(null, new object[] { read, secondId, true, query, 12 }); var count = 0;
                foreach (var match in matches) count++;
                if (count != 1) throw new InvalidDataException("暂存资产中文姓名/别名检索失败");
            }
        }
        finally { context.Unload(); }
    }
}
'@
try {
    [NovelStorageProbe]::Verify($novelPluginPath, $novelProbePath)
    Write-Output '开发暂存 SQLite 探针通过：独立 ALC、win-x64 原生库、中文项目创建/读取、两字姓名与别名检索。'
} finally {
    # 仅删除本次生成的三个明确文件，不使用通配符或递归清理临时目录。
    foreach ($novelProbeFile in @($novelProbePath, "$novelProbePath-wal", "$novelProbePath-shm")) {
        if (Test-Path -LiteralPath $novelProbeFile) { Remove-Item -LiteralPath $novelProbeFile }
    }
}
