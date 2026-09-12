using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Import;
using NovelGeneratePlugin.Infrastructure.Persistence;

// 显式开发工具，不纳入普通测试或生产包。输入与输出必须由调用者给出；本工具的 import/benchmark 不访问网络。
if (args.Length != 3 || args[0] is not ("import" or "benchmark"))
    throw new ArgumentException("用法：AnalysisProbe <import|benchmark> <TXT路径或-> <新的输出目录>");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("输出目录必须尚不存在，避免覆盖已有验证资料。");
Directory.CreateDirectory(output);
var input = args[1];
if (args[0] == "benchmark")
{
    input = Path.Combine(output, "synthetic-capacity.txt");
    var text = new StringBuilder();
    for (var chapter = 1; text.Length < AnalysisLimits.MaximumCharacters; chapter++)
        text.Append($"第{chapter}章 容量验证\r\n").Append(string.Concat(Enumerable.Repeat("雾港的邮差沿着长街寻找钟楼。\r\n", 100)));
    text.Length = AnalysisLimits.MaximumCharacters;
    await File.WriteAllTextAsync(input, text.ToString(), new UTF8Encoding(false));
}

var results = new List<object>();
for (var iteration = 0; iteration < (args[0] == "benchmark" ? 3 : 1); iteration++)
{
    var store = new ReferenceSourceStore(new WorkspacePaths(Path.Combine(output, "run-" + iteration)));
    var service = new NovelImportService(new TxtSourceReader(), store);
    var baselineMemory = GC.GetTotalMemory(true); var maximumMemory = baselineMemory;
    using var sampling = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                maximumMemory = Math.Max(maximumMemory, GC.GetTotalMemory(false));
                await Task.Delay(10, sampling.Token);
            }
        }
        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
    });
    var watch = Stopwatch.StartNew();
    var preview = await service.PreviewAsync(input, null, new(), default);
    var saved = await service.ImportAsync(preview, default);
    var importMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart(); var reopened = store.Read(saved.Id); var reopenMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart(); store.List(); var listMilliseconds = watch.Elapsed.TotalMilliseconds;
    sampling.Cancel(); await sampler;
    var result = new
    {
        Iteration = iteration, saved.Id, saved.Name, saved.Characters,
        Bytes = preview.Import.Source.Bytes.Length, preview.Import.Source.ByteHash,
        preview.Import.Source.EncodingName, Sections = reopened.Sections.Length, Chunks = reopened.Chunks.Length,
        CoveredCharacters = reopened.Chunks.Sum(c => c.Body.Length), preview.Warnings,
        ImportMilliseconds = importMilliseconds, ReopenMilliseconds = reopenMilliseconds, ListMilliseconds = listMilliseconds,
        AdditionalManagedMiB = (maximumMemory - baselineMemory) / 1048576d,
        Machine = Environment.MachineName, Processors = Environment.ProcessorCount, Runtime = Environment.Version.ToString(),
        SourceScope = args[0] == "benchmark" ? "synthetic capacity only" : "complete provided file; original novel coverage unverified"
    };
    results.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
await File.WriteAllTextAsync(Path.Combine(output, "import-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
