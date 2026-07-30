// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Scratch perf harness for fused vs two-phase root hashing. Not for commit.
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class ScratchFusedHashPerfTests
{
    private sealed class EagerTree(IScopedTrieStore trieStore)
        : PatriciaTree(trieStore, LimboLogs.Instance)
    {
        public void BulkSetAndUpdateRoot(in ArrayPoolListRef<BulkSetEntry> entries) =>
            BulkSetAndUpdateRootHash(in entries);
    }

    [Test]
    public void CompareFusedVsTwoPhase()
    {
        const int baseSize = 300_000;
        const int batchSize = 2_000;
        const int iterations = 40;

        EagerTree treeA = new(new RawScopedTrieStore(new TestMemDb())); // two-phase
        EagerTree treeB = new(new RawScopedTrieStore(new TestMemDb())); // fused (depth-2)

        Random rng = new(42);
        byte[][] baseKeys = new byte[baseSize][];
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> baseEntries = new(baseSize))
        {
            HashSet<Hash256> seen = new(baseSize);
            for (int i = 0; i < baseSize; i++)
            {
                byte[] k = new byte[32];
                rng.NextBytes(k);
                Hash256 h = new(k);
                if (!seen.Add(h)) { i--; continue; }
                baseKeys[i] = k;
                byte[] v = new byte[32];
                rng.NextBytes(v);
                baseEntries.Add(new PatriciaTree.BulkSetEntry(h, v));
            }

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> copy = new(baseSize);
            for (int i = 0; i < baseEntries.Count; i++) copy.Add(baseEntries[i]);

            treeA.BulkSet(baseEntries);
            treeA.Commit();
            treeB.BulkSet(copy);
            treeB.Commit();
        }

        Assert.That(treeB.RootHash, Is.EqualTo(treeA.RootHash));

        double[] twoPhaseMs = new double[iterations];
        double[] fusedMs = new double[iterations];

        for (int iter = 0; iter < iterations; iter++)
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> batchA = new(batchSize);
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> batchB = new(batchSize);
            HashSet<Hash256> batchKeys = new(batchSize);
            for (int i = 0; i < batchSize; i++)
            {
                byte[] k;
                if ((i & 3) != 0)
                {
                    k = baseKeys[rng.Next(baseSize)];
                }
                else
                {
                    k = new byte[32];
                    rng.NextBytes(k);
                }
                Hash256 h = new(k);
                if (!batchKeys.Add(h)) { i--; continue; }
                byte[] v = new byte[32];
                rng.NextBytes(v);
                batchA.Add(new PatriciaTree.BulkSetEntry(h, v));
                batchB.Add(new PatriciaTree.BulkSetEntry(h, v));
            }

            Stopwatch sw = new();
            if ((iter & 1) == 0)
            {
                sw.Restart();
                treeA.BulkSet(batchA);
                treeA.UpdateRootHash();
                sw.Stop();
                twoPhaseMs[iter] = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                treeB.BulkSetAndUpdateRoot(batchB);
                sw.Stop();
                fusedMs[iter] = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                sw.Restart();
                treeB.BulkSetAndUpdateRoot(batchB);
                sw.Stop();
                fusedMs[iter] = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                treeA.BulkSet(batchA);
                treeA.UpdateRootHash();
                sw.Stop();
                twoPhaseMs[iter] = sw.Elapsed.TotalMilliseconds;
            }

            Assert.That(treeB.RootHash, Is.EqualTo(treeA.RootHash), $"root divergence at iter {iter}");

            treeA.Commit();
            treeB.Commit();
        }

        Array.Sort(twoPhaseMs);
        Array.Sort(fusedMs);
        string result =
            $"two-phase   : p25={twoPhaseMs[iterations / 4]:F3} median={twoPhaseMs[iterations / 2]:F3} p75={twoPhaseMs[3 * iterations / 4]:F3} ms\n" +
            $"fused-depth2: p25={fusedMs[iterations / 4]:F3} median={fusedMs[iterations / 2]:F3} p75={fusedMs[3 * iterations / 4]:F3} ms\n";
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fused_perf_result.txt"), result);
    }
}
