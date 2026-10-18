// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Cold and warm component measurement of the asynchronous state prefetch, through the production
// FlatWorldStateScope on a flat-state RocksDB far larger than its block cache.
//
// Both arms run the same binary; FlatDb.AsyncReadPrefetch is the only difference. Each round uses one
// read set for all its runs, in ABBA order, so arms are paired. Cold runs drop the OS page cache and reopen
// the database before each run, so the block cache is empty too.
//
// bal-*:      HintBal over 256 accounts with 8 slots each plus 256 accounts without slots (512 + 2048 reads).
// accounts-*: the non-BAL address warm pattern: prefetch, then 8 threads read 512 accounts.
// *-cold:     every read starts cold.  *-partial: 60% of the read set is warmed first.
// *-warm:     everything is cached; measures what the prefetch costs when it cannot help.

using System.Diagnostics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Db;
using Nethermind.State.Flat.ScopeProvider;

Dictionary<string, string> options = new();
for (int i = 0; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];
string Option(string name, string fallback) => options.TryGetValue(name, out string? value) ? value : fallback;

string mode = Option("mode", "all");
string data = Option("data", Path.Combine(Path.GetTempPath(), "async-prefetch-db"));
int accounts = int.Parse(Option("accounts", "2000000"));
int rounds = int.Parse(Option("rounds", "8"));
bool drop = bool.Parse(Option("drop", "true"));
string output = Option("out", "async-prefetch-results.json");
string[] cases = Option("cases", "bal-cold,bal-partial,bal-warm,accounts-cold,accounts-partial,accounts-warm").Split(',');

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
    Dictionary<bool, List<double>> samples = new() { [false] = [], [true] = [] };
    Dictionary<bool, long> prefetched = new() { [false] = 0, [true] = 0 };
    Dictionary<bool, long> skipped = new() { [false] = 0, [true] = 0 };
    Random random = new(HashCode.Combine(name, 20261018));

    for (int round = 0; round < rounds; round++)
    {
        ReadSet set = bal ? ReadSet.Create(accounts, 256, 256, 8, random) : ReadSet.Create(accounts, 0, 512, 0, random);
        bool[] order = round % 2 == 0 ? [false, true, true, false] : [true, false, false, true];
        if (temperature == "warm") Run(set, prefetch: false, bal);

        foreach (bool prefetch in order)
        {
            if (temperature != "warm")
            {
                fixture.ReopenCold();
                if (temperature == "partial") WarmPart(set, bal);
            }

            long before = Metrics.PrefetchedKeys;
            long skippedBefore = Metrics.PrefetchesSkippedCached;
            (double us, int mismatches) = Run(set, prefetch, bal);
            prefetched[prefetch] += Metrics.PrefetchedKeys - before;
            skipped[prefetch] += Metrics.PrefetchesSkippedCached - skippedBefore;
            if (mismatches != 0)
            {
                failures++;
                Console.Error.WriteLine($"FAILED {name} round {round} prefetch={prefetch}: {mismatches} mismatches");
            }
            samples[prefetch].Add(us);
        }
        Console.WriteLine($"{name} round {round + 1}/{rounds}: off={samples[false][^2..].Average():F0}us on={samples[true][^2..].Average():F0}us");
    }

    double offMedian = Median(samples[false]);
    double onMedian = Median(samples[true]);
    rows.Add(new CaseResult(name,
        new ArmResult(offMedian, samples[false].Average(), Percentile(samples[false], 0.9), samples[false], prefetched[false], skipped[false]),
        new ArmResult(onMedian, samples[true].Average(), Percentile(samples[true], 0.9), samples[true], prefetched[true], skipped[true]),
        (onMedian / offMedian - 1) * 100, offMedian / onMedian));
    File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    // A warm database closes the gate, so only cold cases must prefetch; no case may prefetch with the flag off.
    if (prefetched[false] != 0 || (temperature != "warm" && prefetched[true] == 0))
    {
        failures++;
        Console.Error.WriteLine($"FAILED {name}: prefetched keys off={prefetched[false]} on={prefetched[true]}");
    }
}

Console.WriteLine();
Console.WriteLine("| case | off median us | on median us | time change | speedup | off p90 | on p90 | prefetched keys | skipped as cached |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (CaseResult row in rows)
    Console.WriteLine($"| {row.Name} | {row.Off.Median:F0} | {row.On.Median:F0} | {row.TimeChangePercent:+0.0;-0.0}% | {row.Speedup:F2}x | {row.Off.P90:F0} | {row.On.P90:F0} | {row.On.PrefetchedKeys} | {row.On.Skipped} |");
return failures == 0 ? 0 : 2;

(double Us, int Mismatches) Run(ReadSet set, bool prefetch, bool bal)
{
    using FlatWorldStateScope scope = fixture.OpenScope(prefetch);
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
    long begin = Stopwatch.GetTimestamp();
    // As BlockCachePreWarmer.AddressWarmer does: prefetched first, inline, then read.
    scope.Prefetch(set.Addresses, []);
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

record ArmResult(double Median, double Mean, double P90, List<double> Samples, long PrefetchedKeys, long Skipped);
record CaseResult(string Name, ArmResult Off, ArmResult On, double TimeChangePercent, double Speedup);
