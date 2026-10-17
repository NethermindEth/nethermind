using System.Diagnostics;
using System.Text.Json;
using Nethermind.Db;

bool detailedMetrics = Environment.GetEnvironmentVariable("DETAILED_METRICS") == "1";
Metrics.DetailedMetricsEnabled = detailedMetrics;
Console.WriteLine($"Detailed metrics: {detailedMetrics}");
bool verifyOnly = args.Contains("--verify", StringComparer.Ordinal);
string expectedMode = Environment.GetEnvironmentVariable("TRIE_MODE") ?? throw new InvalidOperationException("TRIE_MODE is required");
#if BATCH_TRIE
const string compiledMode = "batch";
#else
const string compiledMode = "scalar";
#endif
if (compiledMode != expectedMode) throw new InvalidOperationException($"Wrong compiled mode: {compiledMode} != {expectedMode}");
Console.WriteLine($"Compiled mode={compiledMode}");
string output = Environment.GetEnvironmentVariable("TRIE_OUTPUT") ?? "trie-results.json";
List<object> rows = [];
foreach (int count in new[] { 1, 8, 64, 65, 512 })
foreach (string shape in new[] { "state", "storage", "shared" })
{
    using TriePrefetchFixture fixture = new(count, shape);
    string[] cacheStates = count == 64
        ? ["warm-db", "scalar-prewarmed", "retained-nodes", "cold-db", "retained-prefix-0", "retained-prefix-1", "retained-prefix-56", "retained-prefix-57", "retained-prefix-63"]
        : ["warm-db", "scalar-prewarmed", "retained-nodes", "cold-db"];
    foreach (string cache in cacheStates)
    {
        Measure(count, shape, cache, () => fixture.Prepare(cache, probe: true), () => fixture.Prepare(cache));
    }
}
foreach (int count in new[] { 1, 8, 64, 512 })
foreach (int contracts in new[] { 1, 8, 64 })
{
    if (contracts > count) continue;
    using TrieCohortFixture fixture = new(count, contracts);
    foreach (string cache in new[] { "warm-db", "dedup-hints", "cold-db" })
        Measure(count, $"cohort-{contracts}", cache, () => fixture.Prepare(cache, probe: true), () => fixture.Prepare(cache));
}

void Measure(int count, string shape, string cache, Func<IMeasuredIteration> verify, Func<IMeasuredIteration> prepare)
{
    using (IMeasuredIteration iteration = verify()) iteration.Verify();
    Console.WriteLine($"Verified Count={count} Shape={shape} Cache={cache}");
    if (verifyOnly) return;
    if (cache != "cold-db")
        for (int i = 0; i < 3; i++)
        {
            using IMeasuredIteration iteration = prepare();
            iteration.Run();
        }
    List<object> samples = [];
    int sampleCount = cache == "cold-db" ? 8 : 12;
    for (int sample = 0; sample < sampleCount; sample++)
    {
        double totalNanoseconds = 0;
        long totalBytes = 0;
        int operations = 0;
        Stopwatch wall = Stopwatch.StartNew();
        do
        {
            using IMeasuredIteration iteration = prepare();
            long bytes = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            iteration.Run();
            totalNanoseconds += Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            totalBytes += GC.GetTotalAllocatedBytes(precise: true) - bytes;
            operations++;
        } while (cache != "cold-db" && wall.Elapsed.TotalSeconds < 0.3);
        samples.Add(new { nanoseconds = totalNanoseconds / operations, managedBytes = (double)totalBytes / operations, operations });
    }
    rows.Add(new { count, shape, cache, detailedMetrics, samples });
    File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
}

interface IMeasuredIteration : IDisposable
{
    void Run();
    void Verify();
}
