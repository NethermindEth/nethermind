using System.Diagnostics;
using System.Text.Json;

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
    foreach (string cache in new[] { "warm-db", "warm-trie", "cold-db" })
    {
        using (TriePrefetchFixture.Iteration iteration = fixture.Prepare(cache, probe: true)) iteration.Verify();
        Console.WriteLine($"Verified Count={count} Shape={shape} Cache={cache}");
        if (verifyOnly) continue;
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
                using TriePrefetchFixture.Iteration iteration = fixture.Prepare(cache);
                long bytes = GC.GetTotalAllocatedBytes(precise: true);
                long start = Stopwatch.GetTimestamp();
                iteration.Run();
                totalNanoseconds += Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                totalBytes += GC.GetTotalAllocatedBytes(precise: true) - bytes;
                operations++;
            } while (cache != "cold-db" && wall.Elapsed.TotalSeconds < 0.3);
            samples.Add(new { nanoseconds = totalNanoseconds / operations, managedBytes = (double)totalBytes / operations, operations });
        }
        rows.Add(new { count, shape, cache, samples });
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
