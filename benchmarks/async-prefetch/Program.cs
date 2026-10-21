// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// MEASUREMENT BRANCH ONLY (bench/prefetch-pagecache-gate). Component measurement of the state prefetch through the
// production FlatWorldStateScope on a flat-state RocksDB far larger than its block cache. Four arms in one binary:
//   off, off-again - no prefetch, twice: their difference is the noise of this run;
//   master         - the master sampler: eight probes, any block-cache miss sends the column (keys are collected
//                    after the decision here, so on cached blocks this arm is cheaper than master itself);
//   gated          - a sampled miss is read once and timed, and only a disk-speed read sends the prefetch.
// One run is a sequence of blocks on one open database and one persistence, as on a node, so state kept across
// blocks is measured in its steady state. Each round uses the same blocks for every run; the arm order rotates per
// round and is mirrored, so each arm runs twice per round in different positions.
//
// bal-*:       HintBal over 256 accounts with 8 slots each plus 256 accounts without slots (512 + 2048 reads).
// accounts-*:  the non-BAL address warm pattern: prefetch, then 8 threads read 512 accounts.
// *-cold:      the OS page cache is dropped and the database reopened before the sequence.
// *-partial:   as cold, then a random 60% of every block's reads is warmed.
// *-pagecache: every block is read once, then the database is reopened without dropping the OS page cache.
// *-mixed:     the OS page cache is dropped, a random half of every block's reads is read once, then the database is
//              reopened without dropping it: half of the misses come from the page cache and half from disk.
// *-warm:      every block is read once before the sequence; everything is cached.

using System.Diagnostics;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;

Dictionary<string, string> options = new();
for (int i = 0; i + 1 < args.Length; i += 2) options[args[i].TrimStart('-')] = args[i + 1];
string Option(string name, string fallback) => options.TryGetValue(name, out string? value) ? value : fallback;

string mode = Option("mode", "all");
string data = Option("data", Path.Combine(Path.GetTempPath(), "async-prefetch-db"));
int accounts = int.Parse(Option("accounts", "2000000"));
int rounds = int.Parse(Option("rounds", "10"));
int blocks = int.Parse(Option("blocks", "8"));
bool drop = bool.Parse(Option("drop", "true"));
string output = Option("out", "async-prefetch-results.json");
string[] cases = Option("cases", "bal-cold,bal-partial,bal-mixed,bal-pagecache,bal-warm,accounts-cold,accounts-partial,accounts-mixed,accounts-pagecache,accounts-warm").Split(',');
Arm[] arms = [Arm.Off, Arm.OffAgain, Arm.Master, Arm.Gated];

using ColdStateFixture fixture = new(data, drop);
if (mode is "build" or "all") fixture.Build(accounts);
if (mode is not ("run" or "all")) return 0;
if (mode == "run") fixture.OpenExisting(accounts);

Console.WriteLine($"accounts={accounts:N0} rounds={rounds} blocks={blocks} drop={drop} cores={Environment.ProcessorCount} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
List<CaseResult> rows = [];
int failures = 0;

foreach (string name in cases)
{
    bool bal = name.StartsWith("bal", StringComparison.Ordinal);
    string temperature = name[(name.IndexOf('-') + 1)..];
    Dictionary<Arm, List<double>> samples = arms.ToDictionary(static a => a, static _ => new List<double>());
    Dictionary<Arm, List<double>> allocated = arms.ToDictionary(static a => a, static _ => new List<double>());
    Dictionary<Arm, long> prefetched = arms.ToDictionary(static a => a, static _ => 0L);
    Random random = new(HashCode.Combine(name, 20261021));

    for (int round = 0; round < rounds; round++)
    {
        ReadSet[] sequence = new ReadSet[blocks];
        for (int b = 0; b < blocks; b++)
            sequence[b] = bal ? ReadSet.Create(accounts, 256, 256, 8, random) : ReadSet.Create(accounts, 0, 512, 0, random);
        Arm[] rotated = [.. arms.Skip(round % arms.Length), .. arms.Take(round % arms.Length)];
        Arm[] order = [.. rotated, .. rotated.Reverse()];

        foreach (Arm arm in order)
        {
            switch (temperature)
            {
                case "warm":
                    foreach (ReadSet set in sequence) Run(set, Arm.Off, bal);
                    break;
                case "pagecache":
                    foreach (ReadSet set in sequence) Run(set, Arm.Off, bal);
                    fixture.ReopenCold(keepPageCache: true);
                    break;
                case "mixed":
                    fixture.ReopenCold();
                    foreach (ReadSet set in sequence) WarmPart(set, bal, half: true);
                    fixture.ReopenCold(keepPageCache: true);
                    break;
                default:
                    fixture.ReopenCold();
                    if (temperature == "partial")
                        foreach (ReadSet set in sequence) WarmPart(set, bal);
                    break;
            }

            long before = Metrics.PrefetchedKeys;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            double total = 0;
            foreach (ReadSet set in sequence)
            {
                (double us, int mismatches) = Run(set, arm, bal);
                total += us;
                if (mismatches != 0)
                {
                    failures++;
                    Console.Error.WriteLine($"FAILED {name} round {round} arm={arm}: {mismatches} mismatches");
                }
            }
            allocated[arm].Add((GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)blocks);
            prefetched[arm] += Metrics.PrefetchedKeys - before;
            samples[arm].Add(total / blocks);
        }
        Console.WriteLine($"{name} round {round + 1}/{rounds}: " + string.Join(" ", arms.Select(a => $"{a}={samples[a][^2..].Average():F0}us")));
    }

    ArmResult Result(Arm arm) => new(Median(samples[arm]), samples[arm].Average(), Percentile(samples[arm], 0.9), Median(allocated[arm]), samples[arm], prefetched[arm]);
    rows.Add(new CaseResult(name, Result(Arm.Off), Result(Arm.OffAgain), Result(Arm.Master), Result(Arm.Gated)));
    File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    bool diskCase = temperature is "cold" or "partial" or "mixed";
    if (prefetched[Arm.Off] + prefetched[Arm.OffAgain] != 0 || (diskCase && (prefetched[Arm.Master] == 0 || prefetched[Arm.Gated] == 0)))
    {
        failures++;
        Console.Error.WriteLine($"FAILED {name}: prefetched keys off={prefetched[Arm.Off]}/{prefetched[Arm.OffAgain]} master={prefetched[Arm.Master]} gated={prefetched[Arm.Gated]}");
    }
}

static string Change(double from, double to) => $"{(to / from - 1) * 100:+0.0;-0.0}%";

Console.WriteLine();
Console.WriteLine("Per-block medians over rounds; off is the mean of the off and off-again medians, and A/A is their difference.");
Console.WriteLine("| case | off us | A/A | master us | gated us | master vs off | gated vs off | gated vs master | gated p90 | master alloc B | gated alloc B | gated prefetched keys |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (CaseResult r in rows)
{
    double off = (r.Off.Median + r.OffAgain.Median) / 2;
    Console.WriteLine($"| {r.Name} | {off:F0} | {Change(r.Off.Median, r.OffAgain.Median)} | {r.Master.Median:F0} | {r.Gated.Median:F0} | {Change(off, r.Master.Median)} | {Change(off, r.Gated.Median)} | {Change(r.Master.Median, r.Gated.Median)} | {r.Gated.P90:F0} | {r.Master.AllocatedBytes:F0} | {r.Gated.AllocatedBytes:F0} | {r.Gated.PrefetchedKeys} |");
}
return failures == 0 ? 0 : 2;

(double Us, int Mismatches) Run(ReadSet set, Arm arm, bool bal)
{
    BasePersistence.BenchDisablePageCacheGate = arm == Arm.Master;
    using FlatWorldStateScope scope = fixture.OpenScope(prefetch: arm is Arm.Master or Arm.Gated);
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

void WarmPart(ReadSet set, bool bal, bool half = false)
{
    using FlatWorldStateScope scope = fixture.OpenScope(prefetch: false);
    // The mixed case reads every other key, so half of the block's reads are in the page cache.
    bool Warm(int k) => half ? k % 2 == 0 : set.WarmMask[k];
    if (bal)
    {
        ReadOnlyAccountChanges[] all = [.. set.Bal.AccountChanges.AsSpan()];
        ReadOnlyAccountChanges[] part = [.. all.Where((_, k) => Warm(k))];
        scope.HintBal(new ReadOnlyBlockAccessList(part, part.Length), new VerifyingSink(set.Indexes)).GetAwaiter().GetResult();
        return;
    }
    for (int k = 0; k < set.Addresses.Length; k++)
    {
        if (Warm(k)) scope.Get(set.Addresses[k]);
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

enum Arm { Off, OffAgain, Master, Gated }

record ArmResult(double Median, double Mean, double P90, double AllocatedBytes, List<double> Samples, long PrefetchedKeys);
record CaseResult(string Name, ArmResult Off, ArmResult OffAgain, ArmResult Master, ArmResult Gated);
