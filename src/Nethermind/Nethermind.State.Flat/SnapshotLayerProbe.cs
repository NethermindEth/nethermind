// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Logging;

namespace Nethermind.State.Flat;

/// <summary>
/// Measurement probe: counts how deep reads walk the in-memory snapshot layers, how often layer probes hit,
/// a sampled cost per probe, and GC activity per block. Prints one Info line per added base snapshot.
/// </summary>
internal static class SnapshotLayerProbe
{
    public const int Account = 0;
    public const int Slot = 1;
    public const int StateNode = 2;
    public const int StorageNode = 3;
    private const int Kinds = 4;

    // Per kind: bundle reads, samples dropped as outliers, reads that walked layers,
    // read-only bundle entries, then per content type (0 = mutable, 1 = sorted): probes, hits, sampled ticks, samples.
    private const int Reads = 0;
    private const int Outliers = 1;
    private const int Walked = 2;
    private const int ReadOnlyEntries = 3;
    private const int PerContent = 4;
    private const int Probes = 0;
    private const int Hits = 1;
    private const int SampledTicks = 2;
    private const int Samples = 3;
    private const int Stride = PerContent + 2 * 4;
    private const int Slots = Kinds * Stride;
    private const int SampleMask = 63;

    private static readonly ConcurrentBag<long[]> _all = [];
    private static readonly long[] _last = new long[Slots];
    private static readonly double _timestampFloorNs = CalibrateFloorNs();
    private static readonly long _outlierTicks = Stopwatch.Frequency / 200_000;
    private static readonly Lock _reportLock = new();

    [ThreadStatic] private static long[]? _local;
    [ThreadStatic] private static int _tick;

    private static long _lastGen0;
    private static long _lastGen1;
    private static long _lastGen2;
    private static TimeSpan _lastPause;
    private static long _lastAllocated;
    private static long _blocks;

    private static long[] Local
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long[]? local = _local;
            if (local is null)
            {
                local = _local = new long[Slots];
                _all.Add(local);
            }

            return local;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Read(int kind) => Local[kind * Stride + Reads]++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WalkLayers(int kind) => Local[kind * Stride + Walked]++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadOnlyEntry(int kind) => Local[kind * Stride + ReadOnlyEntries]++;

    /// <summary>Returns a start timestamp for one probe in every 64 per thread, otherwise 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long StartProbe() => (++_tick & SampleMask) == 0 ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndProbe(int kind, bool sorted, bool found, long start)
    {
        long[] local = Local;
        int at = kind * Stride + PerContent + (sorted ? 4 : 0);
        local[at + Probes]++;
        if (found) local[at + Hits]++;
        if (start != 0)
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            // A sample this long means the thread was descheduled mid-probe; keep it out of the mean.
            if (elapsed > _outlierTicks)
            {
                local[kind * Stride + Outliers]++;
            }
            else
            {
                local[at + SampledTicks] += elapsed;
                local[at + Samples]++;
            }
        }
    }

    public static void ReportBlock(ILogger logger, int baseSnapshots, int compactedSnapshots, long inMemoryBytes, long mutableEntries)
    {
        if (!logger.IsInfo) return;

        lock (_reportLock)
        {
            long[] total = new long[Slots];
            foreach (long[] local in _all)
            {
                for (int i = 0; i < Slots; i++) total[i] += Volatile.Read(ref local[i]);
            }

            long[] delta = new long[Slots];
            for (int i = 0; i < Slots; i++)
            {
                delta[i] = total[i] - _last[i];
                _last[i] = total[i];
            }

            long gen0 = GC.CollectionCount(0);
            long gen1 = GC.CollectionCount(1);
            long gen2 = GC.CollectionCount(2);
            TimeSpan pause = GC.GetTotalPauseDuration();
            long allocated = GC.GetTotalAllocatedBytes(false);

            StringBuilder sb = new();
            sb.Append("LayerProbe block=").Append(++_blocks)
                .Append(" layers=").Append(baseSnapshots).Append('/').Append(compactedSnapshots)
                .Append(" memMB=").Append(inMemoryBytes >> 20)
                .Append(" mutEntries=").Append(mutableEntries)
                .Append(" gc=").Append(gen0 - _lastGen0).Append('/').Append(gen1 - _lastGen1).Append('/').Append(gen2 - _lastGen2)
                .Append(" pauseUs=").Append((long)(pause - _lastPause).TotalMicroseconds)
                .Append(" allocKB=").Append((allocated - _lastAllocated) >> 10)
                .Append(" floorNs=").Append(_timestampFloorNs.ToString("F1"));

            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;
            _lastPause = pause;
            _lastAllocated = allocated;

            ReadOnlySpan<string> names = ["acc", "slot", "snode", "stnode"];
            for (int kind = 0; kind < Kinds; kind++)
            {
                int b = kind * Stride;
                sb.Append(' ').Append(names[kind])
                    .Append("={r=").Append(delta[b + Reads])
                    .Append(",walk=").Append(delta[b + Walked])
                    .Append(",ro=").Append(delta[b + ReadOnlyEntries])
                    .Append(",out=").Append(delta[b + Outliers]);
                AppendContent(sb, "m", delta, b + PerContent);
                AppendContent(sb, "s", delta, b + PerContent + 4);
                sb.Append('}');
            }

            logger.Info(sb.ToString());
        }
    }

    // Probes, hits, sampled ticks and samples; the ticks are kept raw so averages can be taken over many blocks.
    private static void AppendContent(StringBuilder sb, string tag, long[] delta, int at) =>
        sb.Append(',').Append(tag).Append('=')
            .Append(delta[at + Probes]).Append('/')
            .Append(delta[at + Hits]).Append('/')
            .Append((long)TicksToNs(delta[at + SampledTicks])).Append('/')
            .Append(delta[at + Samples]);

    private static double TicksToNs(long ticks) => ticks * 1_000_000_000.0 / Stopwatch.Frequency;

    // Mean cost of a back-to-back timestamp pair, which every sampled probe time includes.
    private static double CalibrateFloorNs()
    {
        const int Pairs = 200_000;
        long sum = 0;
        for (int i = 0; i < Pairs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            sum += Stopwatch.GetTimestamp() - start;
        }

        return TicksToNs(sum) / Pairs;
    }
}
