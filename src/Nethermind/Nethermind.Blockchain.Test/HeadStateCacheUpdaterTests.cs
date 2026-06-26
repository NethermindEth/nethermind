// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class HeadStateCacheUpdaterTests
{
    private static readonly Address A = TestItem.AddressA;

    [Test]
    public void Advances_cache_from_journal_delta_on_sequential_head()
    {
        HeadStateCache cache = new(depth: 2, accountSetsBits: 8, storageSetsBits: 8);
        HeadStateDeltaBuffer buffer = new();
        FakeStateReader reader = new();
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        _ = new HeadStateCacheUpdater(blockTree, cache, reader, LimboLogs.Instance, buffer);

        // First head: no parent in cache -> flush, anchors at block0.
        Block block0 = BlockWith(TestItem.KeccakA, parent: TestItem.KeccakH, stateRoot: TestItem.KeccakC, number: 0);
        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block0));
        Assert.That(cache.HeadHash, Is.EqualTo(TestItem.KeccakA));

        // Warm the cache with the pre-block (block0) values for a hot account + slot.
        StorageCell hotSlot = new(A, (UInt256)1);
        cache.Accounts.Set(A, new Account(1, 100));
        cache.Storage.Set(in hotSlot, [11]);

        // block1 changes A's balance and slot 1; the journal delta carries both the changed account and slot.
        Hash256 root1 = TestItem.KeccakD;
        reader.Accounts[A] = new AccountStruct(2, (UInt256)999);
        reader.Storage[(A, (UInt256)1)] = [99];
        buffer.Store(1, root1, new HeadStateBlockDelta(Accounts(A), Slots(hotSlot), RequiresFlush: false));

        Block block1 = BlockWith(TestItem.KeccakB, parent: TestItem.KeccakA, stateRoot: root1, number: 1);

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.HeadHash, Is.EqualTo(TestItem.KeccakB));

            Assert.That(cache.Accounts.TryGetValue(A, out Account? account), Is.True);
            Assert.That(account!.Balance, Is.EqualTo((UInt256)999), "changed account refreshed to new value");

            Assert.That(cache.Storage.TryGetValue(in hotSlot, out byte[]? slot), Is.True);
            Assert.That(slot, Is.EqualTo(new byte[] { 99 }), "changed slot refreshed to new value");
        }
    }

    [Test]
    public void Flushes_on_self_destruct_delta()
    {
        HeadStateCache cache = new(depth: 2, accountSetsBits: 8, storageSetsBits: 8);
        HeadStateDeltaBuffer buffer = new();
        FakeStateReader reader = new();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        _ = new HeadStateCacheUpdater(blockTree, cache, reader, LimboLogs.Instance, buffer);

        Block block0 = BlockWith(TestItem.KeccakA, parent: TestItem.KeccakH, stateRoot: TestItem.KeccakC, number: 0);
        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block0));

        StorageCell hotSlot = new(A, (UInt256)1);
        cache.Storage.Set(in hotSlot, [11]);

        // RequiresFlush delta: cache must drop everything (can't enumerate cleared slots) and re-anchor.
        Hash256 root1 = TestItem.KeccakD;
        buffer.Store(1, root1, new HeadStateBlockDelta(FrozenSet<AddressAsKey>.Empty, FrozenSet<StorageCell>.Empty, RequiresFlush: true));
        Block block1 = BlockWith(TestItem.KeccakB, parent: TestItem.KeccakA, stateRoot: root1, number: 1);

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block1));

        Assert.That(cache.HeadHash, Is.EqualTo(TestItem.KeccakB), "re-anchored at new head");
        // After a flush the slot is gone from a head (depth 0) lookup until re-read.
        Assert.That(cache.Storage.TryGetValue(in hotSlot, out _), Is.False);
    }

    [Test]
    public void Flushes_on_non_sequential_head()
    {
        HeadStateCache cache = new(depth: 2, accountSetsBits: 8, storageSetsBits: 8);
        HeadStateDeltaBuffer buffer = new();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        _ = new HeadStateCacheUpdater(blockTree, cache, new FakeStateReader(), LimboLogs.Instance, buffer);

        Block block0 = BlockWith(TestItem.KeccakA, parent: TestItem.KeccakH, stateRoot: TestItem.KeccakC, number: 0);
        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block0));

        cache.Accounts.Set(A, new Account(1, 100));

        // Parent does not match current head -> treated as reorg/gap -> flush.
        Block forked = BlockWith(TestItem.KeccakB, parent: TestItem.KeccakF, stateRoot: TestItem.KeccakD, number: 1);
        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(forked));

        Assert.That(cache.HeadHash, Is.EqualTo(TestItem.KeccakB));
        Assert.That(cache.Accounts.TryGetValue(A, out _), Is.False, "flush cleared warmed entries");
    }

    private static FrozenSet<StorageCell> Slots(params StorageCell[] cells) => new HashSet<StorageCell>(cells).ToFrozenSet();

    private static FrozenSet<AddressAsKey> Accounts(params Address[] addresses)
    {
        HashSet<AddressAsKey> set = [];
        foreach (Address address in addresses) set.Add(address);
        return set.ToFrozenSet();
    }

    private static Block BlockWith(Hash256 hash, Hash256 parent, Hash256 stateRoot, long number) =>
        new(Build.A.BlockHeader.WithNumber((ulong)number).WithParentHash(parent).WithStateRoot(stateRoot).WithHash(hash).TestObject);

    private sealed class FakeStateReader : IStateReader
    {
        public Dictionary<Address, AccountStruct> Accounts { get; } = [];
        public Dictionary<(Address, UInt256), byte[]> Storage { get; } = [];

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
            => Accounts.TryGetValue(address, out account);

        public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
            => Storage.TryGetValue((address, index), out byte[]? value) ? value : default;

        public byte[]? GetCode(Hash256 codeHash) => null;
        public byte[]? GetCode(in ValueHash256 codeHash) => null;
        public bool HasStateForBlock(BlockHeader? baseBlock) => true;

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx>
            => throw new NotSupportedException();
    }
}
