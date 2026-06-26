// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using NUnit.Framework;

#nullable enable

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.All)]
public class HeadStateCacheTests
{
    private static readonly Address A = TestItem.AddressA;
    private static readonly Address B = TestItem.AddressB;
    private static readonly Address C = TestItem.AddressC;

    private sealed class Chain
    {
        public IWorldStateScopeProvider Provider { get; } =
            new TrieStoreScopeProvider(new TestRawTrieStore(new TestMemDb()), new TestMemDb(), LimboLogs.Instance);

        public Hash256 Commit(BlockHeader? parent, ulong number, IReadOnlyList<(Address addr, Account account, (UInt256 slot, byte[] value)[] storage)> writes)
        {
            using IWorldStateScopeProvider.IScope scope = Provider.BeginScope(parent, new LocalMetrics());
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(writes.Count))
            {
                foreach ((Address addr, Account account, (UInt256 slot, byte[] value)[] storage) in writes)
                {
                    batch.Set(addr, account);
                    if (storage.Length > 0)
                    {
                        using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(addr, storage.Length);
                        foreach ((UInt256 slot, byte[] value) in storage)
                        {
                            storageBatch.Set(slot, value);
                        }
                    }
                }
            }
            scope.Commit(number);
            return scope.RootHash;
        }

        public Account? GetAccount(BlockHeader header, Address address)
        {
            using IWorldStateScopeProvider.IScope scope = Provider.BeginScope(header, new LocalMetrics());
            return scope.Get(address);
        }

        public byte[] GetStorage(BlockHeader header, Address address, UInt256 slot)
        {
            using IWorldStateScopeProvider.IScope scope = Provider.BeginScope(header, new LocalMetrics());
            return scope.CreateStorageTree(address).Get(slot);
        }
    }

    private sealed class TrieRefresher(IWorldStateScopeProvider provider, BlockHeader header) : IHeadStateRefresher
    {
        public Account? GetAccount(Address address)
        {
            using IWorldStateScopeProvider.IScope scope = provider.BeginScope(header, new LocalMetrics());
            return scope.Get(address);
        }

        public byte[] GetStorage(in StorageCell cell)
        {
            using IWorldStateScopeProvider.IScope scope = provider.BeginScope(header, new LocalMetrics());
            return scope.CreateStorageTree(cell.Address).Get(cell.Index);
        }
    }

    private static BlockHeader Header(Hash256 hash, Hash256? parent, Hash256 stateRoot, long number) =>
        Build.A.BlockHeader.WithNumber((ulong)number).WithStateRoot(stateRoot).WithParentHash(parent!).WithHash(hash).TestObject;

    private static FrozenSet<AddressAsKey> Accounts(params Address[] addresses)
    {
        HashSet<AddressAsKey> set = [];
        foreach (Address a in addresses) set.Add(a);
        return set.ToFrozenSet();
    }

    private static FrozenSet<StorageCell> Slots(params (Address addr, UInt256 slot)[] cells)
    {
        HashSet<StorageCell> set = [];
        foreach ((Address addr, UInt256 slot) in cells) set.Add(new StorageCell(addr, slot));
        return set.ToFrozenSet();
    }

    /// <summary>
    /// Builds a 3-block chain, drives the head cache through two sequential advances, then asserts that
    /// reading any account/storage through the cache-decorated provider at the head and each tracked
    /// ancestor returns exactly what the undecorated trie returns — for both keys the block changed and
    /// keys it left untouched.
    /// </summary>
    [Test]
    public void Decorated_reads_match_trie_at_head_and_ancestors()
    {
        Chain chain = new();

        // Block 0
        Hash256 root0 = chain.Commit(null, 0,
        [
            (A, new Account(1, 100), [((UInt256)1, [11])]),
            (B, new Account(1, 200), [((UInt256)1, [21]), ((UInt256)2, [22])]),
        ]);
        BlockHeader h0 = Header(TestItem.KeccakA, TestItem.KeccakH, root0, 0);

        // Block 1: A balance + A.slot1 change, C created. B untouched.
        Hash256 root1 = chain.Commit(h0, 1,
        [
            (A, new Account(2, 101), [((UInt256)1, [99])]),
            (C, new Account(0, 50), []),
        ]);
        BlockHeader h1 = Header(TestItem.KeccakB, h0.Hash, root1, 1);

        // Block 2: B.slot2 change. A, C untouched.
        Hash256 root2 = chain.Commit(h1, 2,
        [
            (B, new Account(1, 200), [((UInt256)2, [77])]),
        ]);
        BlockHeader h2 = Header(TestItem.KeccakC, h1.Hash, root2, 2);

        HeadStateCache cache = new(depth: 2, accountSetsBits: 8, storageSetsBits: 8);
        HeadStateCacheScopeProvider decorated = new(chain.Provider, cache);

        cache.Flush(h0.Hash!);
        AssertMatches(chain, decorated, h0);

        cache.Advance(h1.Hash!, Accounts(A, C), Slots((A, 1)), new TrieRefresher(chain.Provider, h1));
        AssertMatches(chain, decorated, h1); // depth 0
        AssertMatches(chain, decorated, h0); // depth 1

        cache.Advance(h2.Hash!, Accounts(B), Slots((B, 2)), new TrieRefresher(chain.Provider, h2));
        AssertMatches(chain, decorated, h2); // depth 0
        AssertMatches(chain, decorated, h1); // depth 1
        AssertMatches(chain, decorated, h0); // depth 2
    }

    /// <summary>
    /// Warms the cache at the head, then a reorg flush re-anchors at an unrelated head. Reads at the old
    /// (now non-canonical) header must still be correct via passthrough, and reads at the new head correct.
    /// </summary>
    [Test]
    public void Reorg_flush_keeps_reads_correct()
    {
        Chain chain = new();

        Hash256 root0 = chain.Commit(null, 0, [(A, new Account(1, 100), [((UInt256)1, [11])])]);
        BlockHeader h0 = Header(TestItem.KeccakA, TestItem.KeccakH, root0, 0);

        // Sibling block at the same height with different state.
        Hash256 rootSibling = chain.Commit(null, 0, [(A, new Account(7, 777), [((UInt256)1, [55])])]);
        BlockHeader sibling = Header(TestItem.KeccakD, TestItem.KeccakH, rootSibling, 0);

        HeadStateCache cache = new(depth: 2, accountSetsBits: 8, storageSetsBits: 8);
        HeadStateCacheScopeProvider decorated = new(chain.Provider, cache);

        cache.Flush(h0.Hash!);
        AssertMatches(chain, decorated, h0);

        // Reorg to the sibling: flush re-anchors. Old header now serves via passthrough.
        cache.Flush(sibling.Hash!);
        AssertMatches(chain, decorated, sibling);
        AssertMatches(chain, decorated, h0);
    }

    [Test]
    public void Unknown_or_too_deep_header_passes_through()
    {
        Chain chain = new();
        Hash256 root0 = chain.Commit(null, 0, [(A, new Account(1, 100), [((UInt256)1, [11])])]);
        BlockHeader h0 = Header(TestItem.KeccakA, TestItem.KeccakH, root0, 0);
        BlockHeader unknown = Header(TestItem.KeccakF, TestItem.KeccakH, root0, 0);

        HeadStateCache cache = new(depth: 1, accountSetsBits: 8, storageSetsBits: 8);
        HeadStateCacheScopeProvider decorated = new(chain.Provider, cache);
        cache.Flush(h0.Hash!);

        // Unknown header is not in the ring -> passthrough, still correct.
        AssertMatches(chain, decorated, unknown);
    }

    [Test]
    public void Capture_records_changed_slots_keyed_by_state_root()
    {
        IWorldStateScopeProvider store = new TrieStoreScopeProvider(new TestRawTrieStore(new TestMemDb()), new TestMemDb(), LimboLogs.Instance);
        HeadStateDeltaBuffer buffer = new();
        HeadStateDeltaCaptureScopeProvider capture = new(store, buffer);

        Hash256 root;
        using (IWorldStateScopeProvider.IScope scope = capture.BeginScope(null, new LocalMetrics()))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                batch.Set(A, new Account(1, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(A, 2);
                storage.Set(1, [9]);
                storage.Set(2, [8]);
            }
            scope.Commit(1);
            root = scope.RootHash;
        }

        Assert.That(buffer.TryGet(root, out HeadStateBlockDelta delta), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.RequiresFlush, Is.False);
            Assert.That(delta.ChangedSlots, Does.Contain(new StorageCell(A, (UInt256)1)));
            Assert.That(delta.ChangedSlots, Does.Contain(new StorageCell(A, (UInt256)2)));
            Assert.That(delta.ChangedSlots, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Capture_flags_flush_on_storage_clear()
    {
        IWorldStateScopeProvider store = new TrieStoreScopeProvider(new TestRawTrieStore(new TestMemDb()), new TestMemDb(), LimboLogs.Instance);
        HeadStateDeltaBuffer buffer = new();
        HeadStateDeltaCaptureScopeProvider capture = new(store, buffer);

        Hash256 root;
        using (IWorldStateScopeProvider.IScope scope = capture.BeginScope(null, new LocalMetrics()))
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                batch.Set(A, new Account(1, 100));
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(A, 1);
                storage.Clear(); // self-destruct must be signalled first
                storage.Set(1, [9]);
            }
            scope.Commit(1);
            root = scope.RootHash;
        }

        Assert.That(buffer.TryGet(root, out HeadStateBlockDelta delta), Is.True);
        Assert.That(delta.RequiresFlush, Is.True);
    }

    private static void AssertMatches(Chain chain, HeadStateCacheScopeProvider decorated, BlockHeader header)
    {
        // Run twice so the second pass exercises cache hits/backfill, not just first-touch misses.
        for (int pass = 0; pass < 2; pass++)
        {
            using (Assert.EnterMultipleScope())
            {
                foreach (Address address in new[] { A, B, C })
                {
                    Account? expectedAccount = chain.GetAccount(header, address);
                    Account? actualAccount = ReadAccount(decorated, header, address);
                    Assert.That(actualAccount?.Balance, Is.EqualTo(expectedAccount?.Balance), $"{address} balance @ {header.Number} pass {pass}");
                    Assert.That(actualAccount?.Nonce, Is.EqualTo(expectedAccount?.Nonce), $"{address} nonce @ {header.Number} pass {pass}");

                    foreach (UInt256 slot in new UInt256[] { 1, 2 })
                    {
                        byte[] expected = chain.GetStorage(header, address, slot);
                        byte[] actual = ReadStorage(decorated, header, address, slot);
                        Assert.That(actual, Is.EqualTo(expected), $"{address}.{slot} @ {header.Number} pass {pass}");
                    }
                }
            }
        }
    }

    private static Account? ReadAccount(HeadStateCacheScopeProvider decorated, BlockHeader header, Address address)
    {
        using IWorldStateScopeProvider.IScope scope = decorated.BeginScope(header, new LocalMetrics());
        return scope.Get(address);
    }

    private static byte[] ReadStorage(HeadStateCacheScopeProvider decorated, BlockHeader header, Address address, UInt256 slot)
    {
        using IWorldStateScopeProvider.IScope scope = decorated.BeginScope(header, new LocalMetrics());
        return scope.CreateStorageTree(address).Get(slot);
    }
}
