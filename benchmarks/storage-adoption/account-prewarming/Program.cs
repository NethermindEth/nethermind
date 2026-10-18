using System.Diagnostics;
using System.Text.Json;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;

bool verifyOnly = args.Contains("--verify", StringComparer.Ordinal);
bool detailedMetrics = Environment.GetEnvironmentVariable("DETAILED_METRICS") == "1";
MetricsController metrics = new(new MetricsConfig { Enabled = true, EnableDetailedMetric = detailedMetrics });
metrics.RegisterMetrics(typeof(Nethermind.Db.Metrics));
metrics.RegisterMetrics(typeof(Nethermind.State.Flat.Metrics));
Console.WriteLine($"Detailed metrics: {detailedMetrics}; observers: {Nethermind.Db.Metrics.PrewarmerGetTime.GetType().Name}, {Nethermind.State.Flat.Metrics.ReadOnlySnapshotBundleTimes.GetType().Name}");
if (detailedMetrics && (Nethermind.Db.Metrics.PrewarmerGetTime.GetType().Name.Contains("Noop")
    || Nethermind.State.Flat.Metrics.ReadOnlySnapshotBundleTimes.GetType().Name.Contains("Noop")))
    throw new InvalidOperationException("Detailed metrics must use real production histogram observers");
string output = Environment.GetEnvironmentVariable("ACCOUNT_OUTPUT") ?? "account-results.json";
List<object> rows = [];
List<(int Count, string Shape)> cases = [];
foreach (int count in new[] { 1, 7, 8, 32, 64, 256, 512 })
    foreach (string shape in new[] { "distinct", "repeated", "shared", "mixed" }) cases.Add((count, shape));
foreach (string shape in new[] { "snapshot", "tail1", "tail7" }) cases.Add((64, shape));
foreach (int count in new[] { 8, 64, 512 }) cases.Add((count, "native-warm"));
foreach ((int count, string shape) in cases)
{
    using AccountPrewarmingFixture fixture = new(count, shape);
    fixture.Verify();
    int preflightCacheMisses = fixture.CacheMisses;
    Console.WriteLine($"Verified Count={count} Shape={shape}");
    if (verifyOnly)
    {
        if (shape == "distinct" && count is 64 or 512)
        {
            fixture.ReopenCold();
            fixture.Verify();
            Console.WriteLine($"Verified cold reopen Count={count} Shape={shape}");
        }
        continue;
    }
    Stopwatch warmup = Stopwatch.StartNew();
    while (warmup.Elapsed.TotalSeconds < 0.5) { fixture.Prepare(); fixture.Read(); }
    List<object> samples = [];
    for (int sample = 0; sample < 12; sample++)
    {
        long ticks = 0;
        long allocated = 0;
        int operations = 0;
        do
        {
            fixture.Prepare();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            fixture.Read();
            ticks += Stopwatch.GetTimestamp() - start;
            allocated += GC.GetTotalAllocatedBytes(precise: true) - before;
            operations++;
        } while ((double)ticks / Stopwatch.Frequency < 0.15);
        samples.Add(new
        {
            nanoseconds = (double)ticks / Stopwatch.Frequency * 1e9 / operations,
            managedBytes = (double)allocated / operations,
            operations
        });
    }
    rows.Add(new { count, shape, cache = "warm", detailedMetrics, preflightCacheMisses, samples });
    if (shape == "distinct" && count is 64 or 512)
    {
        samples = [];
        for (int sample = 0; sample < 8; sample++)
        {
            fixture.ReopenCold();
            fixture.Prepare();
            ColdReadDiagnostics diagnosticsBefore = ColdReadDiagnostics.Capture();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            fixture.Read();
            double nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            ColdReadDiagnostics diagnosticsAfter = ColdReadDiagnostics.Capture();
            fixture.VerifyResults();
            samples.Add(new
            {
                nanoseconds, managedBytes = allocated, operations = 1, cacheMisses = fixture.CacheMisses,
                sampleIndex = sample, diagnostics = diagnosticsAfter.Since(diagnosticsBefore)
            });
        }
        rows.Add(new { count, shape, cache = "cold", detailedMetrics, preflightCacheMisses, samples });
    }
    File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
}
