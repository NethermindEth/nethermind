// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class CachedTrieStoreTests
{
    private static readonly TreePath PathA = TreePath.FromHexString("12");
    private static readonly TreePath PathB = TreePath.FromHexString("34");

    [Test]
    public void FindCachedOrUnknown_calls_base_once_per_path_and_hash()
    {
        IScopedTrieStore baseStore = CreateBaseStore();
        CachedTrieStore store = new(baseStore);

        TrieNode first = store.FindCachedOrUnknown(PathA, TestItem.KeccakA);
        TrieNode repeated = store.FindCachedOrUnknown(PathA, TestItem.KeccakA);
        store.FindCachedOrUnknown(PathA, TestItem.KeccakB);
        store.FindCachedOrUnknown(PathB, TestItem.KeccakA);

        Assert.That(repeated, Is.SameAs(first));

        baseStore.Received(1).FindCachedOrUnknown(PathA, TestItem.KeccakA);
        baseStore.Received(1).FindCachedOrUnknown(PathA, TestItem.KeccakB);
        baseStore.Received(1).FindCachedOrUnknown(PathB, TestItem.KeccakA);
    }

    /// <remarks>
    /// Reports the cheapest of several identical measurement windows: a per-lookup cost survives the minimum,
    /// while a one-off chunk the runtime charges to this thread lands in one window only.
    /// </remarks>
    [Test]
    public void FindCachedOrUnknown_cache_hit_does_not_allocate()
    {
        const int Iterations = 1000;
        const int Windows = 5;
        CachedTrieStore store = new(CreateBaseStore());
        TrieNode cached = store.FindCachedOrUnknown(PathA, TestItem.KeccakA);
        for (int i = 0; i < Iterations; i++)
        {
            store.FindCachedOrUnknown(PathA, TestItem.KeccakA);
        }

        long allocated = long.MaxValue;
        for (int window = 0; window < Windows; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                store.FindCachedOrUnknown(PathA, TestItem.KeccakA);
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allocated, Is.Zero, "a cache hit must not allocate");
            Assert.That(store.FindCachedOrUnknown(PathA, TestItem.KeccakA), Is.SameAs(cached));
        }
    }

    private static IScopedTrieStore CreateBaseStore()
    {
        IScopedTrieStore baseStore = Substitute.For<IScopedTrieStore>();
        baseStore.FindCachedOrUnknown(default, default!)
            .ReturnsForAnyArgs(static call => new TrieNode(NodeType.Unknown, call.ArgAt<Hash256>(1)));
        return baseStore;
    }
}
