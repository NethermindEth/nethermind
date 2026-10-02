// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>Timing harness, not a test: the block-end account-trie insert and hash for a large tree.</summary>
[Explicit("timing harness")]
public class BlockEndAccountTrieTiming
{
    [TestCase(1_000_000, 3_000, 15)]
    [TestCase(1_000_000, 600, 15)]
    public void Time_insert_and_hash(int treeSize, int dirty, int rounds)
    {
        MemDb db = new();
        Random random = new(7);
        Address[] addresses = new Address[treeSize];
        StateTree setup = new(new RawScopedTrieStore(db), NullLogManager.Instance);
        byte[] buffer = new byte[20];
        Dictionary<AddressAsKey, Account?> all = new(treeSize);
        for (int i = 0; i < treeSize; i++)
        {
            random.NextBytes(buffer);
            addresses[i] = new Address((byte[])buffer.Clone());
            all[addresses[i]] = new Account((ulong)random.Next(), (UInt256)(ulong)random.NextInt64());
        }

        setup.SetAccounts(all);
        setup.Commit();
        Hash256 root = setup.RootHash;
        TestContext.Out.WriteLine($"tree of {treeSize} built, root {root}");

        List<double> inserts = [], hashes = [];
        for (int r = 0; r < rounds + 2; r++)
        {
            StateTree tree = new(new RawScopedTrieStore(db), NullLogManager.Instance) { RootHash = root };
            Dictionary<AddressAsKey, Account?> changes = new(dirty);
            foreach (int i in Enumerable.Range(0, dirty).Select(_ => random.Next(treeSize)))
            {
                changes[addresses[i]] = new Account((ulong)random.Next(), (UInt256)(ulong)random.NextInt64());
            }

            long t0 = Stopwatch.GetTimestamp();
            tree.SetAccounts(changes);
            long t1 = Stopwatch.GetTimestamp();
            tree.UpdateRootHash();
            long t2 = Stopwatch.GetTimestamp();
            if (r < 2) continue; // warm-up
            inserts.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            hashes.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
        }

        inserts.Sort();
        hashes.Sort();
        TestContext.Out.WriteLine($"dirty {dirty}: insert median {inserts[inserts.Count / 2]:F3} ms (min {inserts[0]:F3}), hash median {hashes[hashes.Count / 2]:F3} ms (min {hashes[0]:F3})");
    }
}
