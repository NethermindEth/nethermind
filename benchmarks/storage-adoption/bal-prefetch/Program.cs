using System.Diagnostics;
using System.Text.Json;
using Nethermind.Db;

bool detailedMetrics = Environment.GetEnvironmentVariable("DETAILED_METRICS") == "1";
Metrics.DetailedMetricsEnabled = detailedMetrics;
Console.WriteLine($"Detailed metrics: {detailedMetrics}");
bool verifyOnly = args.Contains("--verify", StringComparer.Ordinal);
string output = Environment.GetEnvironmentVariable("BAL_OUTPUT") ?? "bal-results.json";
List<object> rows = [];
List<(int Count, string Shape)> cases = [];
foreach (int count in new[] { 1, 7, 8, 9, 63, 64, 65, 512 })
foreach (string shape in new[] { "accounts", "slots", "one-account", "overlay-1", "overlay-7", "overlay-8", "cache" })
    cases.Add((count, shape));
foreach (int count in new[] { 127, 128, 129 }) cases.Add((count, "one-account"));
foreach (int count in new[] { 63, 64, 65, 127, 128, 129 }) cases.Add((count, "one-account-cache"));
foreach ((int count, string shape) in cases)
{
    using BalPrefetchFixture fixture = new(count, shape);
    fixture.Verify();
    Console.WriteLine($"Verified Count={count} Shape={shape}");
    if (verifyOnly)
    {
        if (count is 8 or 64 or 512 && shape is "accounts" or "slots")
        {
            fixture.ReopenCold();
            fixture.Verify();
            Console.WriteLine($"Verified cold Count={count} Shape={shape}");
        }
        continue;
    }

    Stopwatch warmup = Stopwatch.StartNew();
    while (warmup.Elapsed.TotalSeconds < 1) fixture.Read();
    List<object> samples = [];
    for (int sample = 0; sample < 12; sample++)
    {
        long bytes = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        int operations = 0;
        do
        {
            fixture.Read();
            operations++;
        } while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.3);
        double nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds / operations;
        double managedBytes = (double)(GC.GetTotalAllocatedBytes(precise: true) - bytes) / operations;
        samples.Add(new { nanoseconds, managedBytes, operations });
    }
    rows.Add(new { count, shape, cache = "warm", detailedMetrics, samples });

    if (count is 8 or 64 or 512 && shape is "accounts" or "slots")
    {
        samples = [];
        for (int sample = 0; sample < 8; sample++)
        {
            fixture.ReopenCold();
            long bytes = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            fixture.Read();
            double nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            double managedBytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            samples.Add(new { nanoseconds, managedBytes, operations = 1 });
        }
        rows.Add(new { count, shape, cache = "cold", detailedMetrics, samples });
    }
    File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
}
