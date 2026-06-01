// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Sparse;

/// <summary>
/// Smoke tests that run the same code as the BDN benchmarks to verify they don't crash,
/// and measure rough timing for KPI comparison.
/// </summary>
[TestFixture]
public class SparseBenchmarkSmokeTests
{
    [TestCase(10)]
    [TestCase(100)]
    [TestCase(1000)]
    public void EndToEnd_Patricia_vs_Sparse(int n)
    {
        Random rng = new(42);
        Hash256[] keys = new Hash256[n];
        byte[][] values = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            keys[i] = Keccak.Compute(key);
            values[i] = TestItem.GenerateIndexedAccountRlp(i);
        }

        // Patricia baseline
        Stopwatch swPatricia = Stopwatch.StartNew();
        MemDb freshDb = new();
        PatriciaTree tree = new(new RawTrieStore(freshDb).GetTrieStore(null), LimboLogs.Instance);
        for (int i = 0; i < n; i++)
            tree.Set(keys[i].Bytes, values[i]);
        tree.UpdateRootHash();
        tree.Commit();
        Hash256 patriciaRoot = tree.RootHash;
        swPatricia.Stop();

        // Sparse
        Stopwatch swSparse = Stopwatch.StartNew();
        using SparsePatriciaTree sparse = new();
        Dictionary<ValueHash256, LeafUpdate> updates = new(n);
        for (int i = 0; i < n; i++)
            updates[keys[i]] = LeafUpdate.Changed(values[i]);
        sparse.UpdateLeaves(updates, null);
        Hash256 sparseRoot = sparse.ComputeRoot();
        swSparse.Stop();

        // Correctness
        Assert.That(sparseRoot, Is.EqualTo(patriciaRoot), $"N={n}: roots must match");

        // KPI reporting — write to file for reliable access
        double ratio = (double)swSparse.ElapsedTicks / swPatricia.ElapsedTicks;
        string line = $"N={n}: Patricia={swPatricia.ElapsedMilliseconds}ms, Sparse={swSparse.ElapsedMilliseconds}ms, Ratio={ratio:F2}";
        if (n >= 1000)
            line += $" | KPI CHECK: Ratio={ratio:F2} (target <= 1.10)";
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparse_benchmark_smoke.txt"),
            line + Environment.NewLine);
    }
}
