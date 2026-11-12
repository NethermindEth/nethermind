// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public static class OpCoverageProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_OPCOV") != "0";

    private const int MaxItems = 200;

    [ThreadStatic]
    private static Dictionary<ValueHash256, long[]>? _counts;

    private static readonly BlockingCollection<(long Block, Dictionary<ValueHash256, long[]> Counts)> _queue = [];
    private static int _writerStarted;

    public static bool Active => Enabled && ProcessingThread.IsBlockProcessingThread;

    public static void Add(CodeInfo codeInfo, int ops, long ticks)
    {
        try
        {
            Dictionary<ValueHash256, long[]> counts = _counts ??= [];
            ValueHash256 key = codeInfo.CodeHash;
            if (!counts.TryGetValue(key, out long[]? slot))
            {
                slot = new long[3];
                counts[key] = slot;
            }

            slot[0] += ops;
            slot[1] += ticks;
            slot[2]++;
        }
        catch (Exception)
        {
        }
    }

    public static void Flush(long blockNumber)
    {
        if (!Active) return;
        try
        {
            Dictionary<ValueHash256, long[]>? counts = _counts;
            _counts = null;
            if (Interlocked.Exchange(ref _writerStarted, 1) == 0)
            {
                Thread writer = new(Write) { IsBackground = true, Name = "opcov-writer" };
                writer.Start();
            }

            _queue.Add((blockNumber, counts ?? []));
        }
        catch (Exception)
        {
        }
    }

    private static void Write()
    {
        foreach ((long block, Dictionary<ValueHash256, long[]> counts) in _queue.GetConsumingEnumerable())
        {
            try
            {
                List<(ValueHash256 Hash, long[] Slot)> items = new(counts.Count);
                long totalOps = 0;
                long totalTicks = 0;
                long totalSegments = 0;
                foreach (KeyValuePair<ValueHash256, long[]> kv in counts)
                {
                    items.Add((kv.Key, kv.Value));
                    totalOps += kv.Value[0];
                    totalTicks += kv.Value[1];
                    totalSegments += kv.Value[2];
                }

                items.Sort(static (a, b) => b.Slot[0].CompareTo(a.Slot[0]));
                long shownOps = 0;
                long shownTicks = 0;
                StringBuilder sb = new();
                sb.Append("OPCOV block=").Append(block)
                    .Append(" ops=").Append(totalOps)
                    .Append(" us=").Append(totalTicks * 1_000_000 / Stopwatch.Frequency)
                    .Append(" segs=").Append(totalSegments)
                    .Append(" n=").Append(items.Count)
                    .Append(" items=");
                int shown = Math.Min(items.Count, MaxItems);
                for (int i = 0; i < shown; i++)
                {
                    (ValueHash256 hash, long[] slot) = items[i];
                    shownOps += slot[0];
                    shownTicks += slot[1];
                    if (i > 0) sb.Append(';');
                    sb.Append(hash == default ? "unstamped" : Convert.ToHexString(hash.Bytes[..8]))
                        .Append(':').Append(slot[0])
                        .Append(':').Append(slot[1] * 1_000_000 / Stopwatch.Frequency);
                }

                sb.Append(" rest_ops=").Append(totalOps - shownOps)
                    .Append(" rest_us=").Append((totalTicks - shownTicks) * 1_000_000 / Stopwatch.Frequency);
                Console.WriteLine(sb.ToString());
            }
            catch (Exception)
            {
            }
        }
    }
}
