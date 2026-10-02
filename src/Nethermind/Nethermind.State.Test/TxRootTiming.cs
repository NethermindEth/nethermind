// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>Timing harness, not a test: the transactions-trie root of a block, inline and through Task.Run.</summary>
[Explicit("timing harness")]
public class TxRootTiming
{
    [TestCase(100)]
    [TestCase(300)]
    [TestCase(1000)]
    public void Time_tx_root(int txCount)
    {
        Random random = new(3);
        byte[][] encoded = new byte[txCount][];
        for (int i = 0; i < txCount; i++)
        {
            encoded[i] = new byte[random.Next(110, 400)];
            random.NextBytes(encoded[i]);
            encoded[i][0] = 0x02; // typed transaction envelope
        }

        List<double> inline = [], viaTask = [];
        for (int r = 0; r < 220; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            TxTrie.CalculateRoot(encoded);
            long t1 = Stopwatch.GetTimestamp();
            Task.Run(() => TxTrie.CalculateRoot(encoded)).GetAwaiter().GetResult();
            long t2 = Stopwatch.GetTimestamp();
            if (r < 20) continue;
            inline.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            viaTask.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            // Let pool workers go idle between iterations, as between payloads.
            if (r % 10 == 0) System.Threading.Thread.Sleep(50);
        }

        inline.Sort();
        viaTask.Sort();
        TestContext.Out.WriteLine($"{txCount} txs: inline median {inline[inline.Count / 2]:F3} ms, Task.Run median {viaTask[viaTask.Count / 2]:F3} ms (p90 {viaTask[viaTask.Count * 9 / 10]:F3})");
    }
}
