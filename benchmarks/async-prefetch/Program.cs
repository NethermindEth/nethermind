// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// MEASUREMENT BRANCH ONLY (bench/prefetch-async-ab). Component measurement of the state prefetch through the
// production FlatWorldStateScope on a flat-state RocksDB far larger than its block cache, with three arms in one
// binary:
//   off       - the reader reports no prefetch support (master path for a backend that cannot read ahead);
//   multiget  - the released path: MultiGetAsync(byte[][]) with the values discarded;
//   prefetch  - RocksDb.PrefetchAsync from the local-feed bindings package (bindings main b7addd6, not released).
// Each round uses one read set for every run; the arm order rotates per round and each arm runs twice, mirrored,
// so position effects cancel. Cold runs drop the OS page cache and reopen the database, so the block cache is empty.
//
// bal-*:       HintBal over 256 accounts with 8 slots each plus 256 accounts without slots (512 + 2048 reads).
// accounts-*:  the non-BAL address warm pattern: prefetch, then 8 threads read 512 accounts.
// *-cold:      every read starts cold.  *-partial: a random 60% of the read set is warmed first.
// *-pagecache: the block cache is empty but the OS page cache holds the read set.
// *-warm:      everything is cached.

using System.Diagnostics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.State.Flat.ScopeProvider;

Dictionary<string, string> options = new();
for (int i = 0; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];
string Option(string name, string fallback) => options.TryGetValue(name, out string? value) ? value : fallback;

string mode = Option("mode", "all");
string data = Option("data", Path.Combine(Path.GetTempPath(), "async-prefetch-db"));
int accounts = int.Parse(Option("accounts", "2000000"));
int rounds = int.Parse(Option("rounds", "12"));
bool drop = bool.Parse(Option("drop", "true"));
string output = Option("out", "async-prefetch-results.json");
string[] cases = Option("cases", "bal-cold,bal-partial,bal-pagecache,bal-warm,accounts-cold,accounts-partial,accounts-pagecache,accounts-warm").Split(',');
Arm[] arms = [Arm.Off, Arm.MultiGet, Arm.Prefetch];

using ColdStateFixture fixture = new(data, drop);
if (mode is "build" or "all") fixture.Build(accounts);
if (mode is not ("run" or "all")) return 0;
if (mode == "run") fixture.OpenExisting(accounts);

Console.WriteLine($"accounts={accounts:N0} rounds={rounds} drop={drop} cores={Environment.ProcessorCount} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
List<CaseResult> rows = [];
int failures = 0;

foreach (string name in cases)
{
    bool bal = name.StartsWith("bal", StringComparison.Ordinal);
    string temperature = name[(name.IndexOf('-') + 1)..];
    Dictionary<Arm, List<double>> samples = arms.ToDictionary(static a => a, static _ => new List<double>());
    Dictionary<Arm, List<double>> allocated = arms.ToDictionary(static a => a, static _ => new List<double>());
    Dictionary<Arm, long> prefetched = arms.ToDictionary(static a => a, static _ => 0L);
    Dictionary<Arm, long> skipped = arms.ToDictionary(static a => a, static _ => 0L);
    Random random = new(HashCode.Combine(name, 20261021));

    for (int round = 0; round < rounds; round++)
    {
        ReadSet set = bal ? ReadSet.Create(accounts, 256, 256, 8, random) : ReadSet.Create(accounts, 0, 512, 0, random);
        Arm[] rotated = [.. arms.Skip(round % arms.Length), .. arms.Take(round % arms.Length)];
        Arm[] order = [.. rotated, .. rotated.Reverse()];
        if (temperature == "warm") Run(set, Arm.Off, bal);

        foreach (Arm arm in order)
        {
            if (temperature == "pagecache")
            {
                Run(set, Arm.Off, bal);
                fixture.ReopenCold(keepPageCache: true);
            }
            else if (temperature != "warm")
            {
                fixture.ReopenCold();
                if (temperature == "partial") WarmPart(set, bal);
            }

            long before = Metrics.PrefetchedKeys;
            long skippedBefore = Metrics.PrefetchesSkippedCached;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            (double us, int mismatches) = Run(set, arm, bal);
            allocated[arm].Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            prefetched[arm] += Metrics.PrefetchedKeys - before;
            skipped[arm] += Metrics.PrefetchesSkippedCached - skippedBefore;
            if (mismatches != 0)
            {
                failures++;
                Console.Error.WriteLine($"FAILED {name} round {round} arm={arm}: {mismatches} mismatches");
            }
            samples[arm].Add(us);
        }
        Console.WriteLine($"{name} round {round + 1}/{rounds}: " + string.Join(" ", arms.Select(a => $"{a}={samples[a][^2..].Average():F0}us")));
    }

    ArmResult Result(Arm arm) => new(Median(samples[arm]), samples[arm].Average(), Percentile(samples[arm], 0.9), Median(allocated[arm]), samples[arm], prefetched[arm], skipped[arm]);
    CaseResult row = new(name, Result(Arm.Off), Result(Arm.MultiGet), Result(Arm.Prefetch));
    rows.Add(row);
    File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    // The off arm never prefetches; every cold case must prefetch in both prefetch arms.
    bool coldCase = temperature is "cold" or "partial" or "pagecache";
    if (prefetched[Arm.Off] != 0 || (coldCase && (prefetched[Arm.MultiGet] == 0 || prefetched[Arm.Prefetch] == 0)))
    {
        failures++;
        Console.Error.WriteLine($"FAILED {name}: prefetched keys off={prefetched[Arm.Off]} multiget={prefetched[Arm.MultiGet]} prefetch={prefetched[Arm.Prefetch]}");
    }
}

static string Change(double from, double to) => $"{(to / from - 1) * 100:+0.0;-0.0}%";

Console.WriteLine();
Console.WriteLine("| case | off us | multiget us | prefetch us | multiget vs off | prefetch vs off | prefetch vs multiget | multiget p90 | prefetch p90 | multiget alloc B | prefetch alloc B | prefetched keys |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (CaseResult r in rows)
    Console.WriteLine($"| {r.Name} | {r.Off.Median:F0} | {r.MultiGet.Median:F0} | {r.Prefetch.Median:F0} | {Change(r.Off.Median, r.MultiGet.Median)} | {Change(r.Off.Median, r.Prefetch.Median)} | {Change(r.MultiGet.Median, r.Prefetch.Median)} | {r.MultiGet.P90:F0} | {r.Prefetch.P90:F0} | {r.MultiGet.AllocatedBytes:F0} | {r.Prefetch.AllocatedBytes:F0} | {r.Prefetch.PrefetchedKeys} |");
return failures == 0 ? 0 : 2;

(double Us, int Mismatches) Run(ReadSet set, Arm arm, bool bal)
{
    DbOnTheRocks.BenchPrefetchThroughMultiGet = arm == Arm.MultiGet;
    using FlatWorldStateScope scope = fixture.OpenScope(prefetch: arm != Arm.Off);
    if (bal)
    {
        VerifyingSink sink = new(set.Indexes);
        long start = Stopwatch.GetTimestamp();
        scope.HintBal(set.Bal, sink).GetAwaiter().GetResult();
        double us = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        int wrongCounts = sink.AccountReads == set.Addresses.Length && sink.SlotReads == set.ExpectedSlotReads ? 0 : 1;
        return (us, sink.Mismatches + wrongCounts);
    }

    int mismatches = 0;
    // Prefetch may filter its spans in place, so it gets its own copy, made before timing and in every arm.
    Address[] scratch = [.. set.Addresses];
    long begin = Stopwatch.GetTimestamp();
    // As BlockCachePreWarmer.AddressWarmer does: prefetched first, inline, then read.
    if (scope.SupportsPrefetch) scope.Prefetch(scratch, [], CancellationToken.None);
    const int threads = 8;
    int per = (set.Addresses.Length + threads - 1) / threads;
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        for (int k = t * per; k < Math.Min((t + 1) * per, set.Addresses.Length); k++)
        {
            if (scope.Get(set.Addresses[k]) != ColdStateFixture.AccountAt(set.AccountIndexes[k])) Interlocked.Increment(ref mismatches);
        }
    });
    return (Stopwatch.GetElapsedTime(begin).TotalMicroseconds, mismatches);
}

void WarmPart(ReadSet set, bool bal)
{
    using FlatWorldStateScope scope = fixture.OpenScope(prefetch: false);
    if (bal)
    {
        ReadOnlyAccountChanges[] all = [.. set.Bal.AccountChanges.AsSpan()];
        ReadOnlyAccountChanges[] part = [.. all.Where((_, k) => set.WarmMask[k])];
        scope.HintBal(new ReadOnlyBlockAccessList(part, part.Length), new VerifyingSink(set.Indexes)).GetAwaiter().GetResult();
        return;
    }
    for (int k = 0; k < set.Addresses.Length; k++)
    {
        if (set.WarmMask[k]) scope.Get(set.Addresses[k]);
    }
}

static double Median(List<double> values) => Percentile(values, 0.5);

static double Percentile(List<double> values, double p)
{
    double[] sorted = [.. values.Order()];
    double rank = p * (sorted.Length - 1);
    int low = (int)rank;
    int high = Math.Min(low + 1, sorted.Length - 1);
    return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
}

enum Arm { Off, MultiGet, Prefetch }

record ArmResult(double Median, double Mean, double P90, double AllocatedBytes, List<double> Samples, long PrefetchedKeys, long Skipped);
record CaseResult(string Name, ArmResult Off, ArmResult MultiGet, ArmResult Prefetch);
