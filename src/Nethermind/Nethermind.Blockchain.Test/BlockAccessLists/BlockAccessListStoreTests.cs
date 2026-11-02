// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListStoreTests
{
    [Test]
    public void Round_trips_byte_array_payload()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 42;
        Hash256 blockHash = TestItem.KeccakA;
        byte[] encoded = [0xc1, 0x80];

        store.Insert(blockNumber, blockHash, encoded);

        Assert.That(store.Exists(blockNumber, blockHash), Is.True);
        using MemoryManager<byte>? rlp = store.GetRlp(blockNumber, blockHash);
        Assert.That(rlp!.Memory, Is.SequenceEqualTo(encoded));
    }

    [Test]
    public void Round_trips_decoded_block_access_list()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 1_000_000;
        Hash256 blockHash = TestItem.KeccakB;
        ReadOnlyBlockAccessList bal = new();

        store.Insert(blockNumber, blockHash, bal);

        ReadOnlyBlockAccessList? retrieved = store.Get(blockNumber, blockHash);
        Assert.That(retrieved, Is.Not.Null);

        using MemoryManager<byte>? rlp = store.GetRlp(blockNumber, blockHash);
        Assert.That(rlp!.Memory, Is.SequenceEqualTo(Rlp.Encode(bal).Bytes));
    }

    [Test]
    public void Delete_removes_entry()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 5;
        Hash256 blockHash = TestItem.KeccakC;
        byte[] encoded = [0xc1, 0x80];
        store.Insert(blockNumber, blockHash, encoded);

        store.Delete(blockNumber, blockHash);

        Assert.That(store.Exists(blockNumber, blockHash), Is.False);
        Assert.That(store.GetRlp(blockNumber, blockHash), Is.Null);
        Assert.That(store.Get(blockNumber, blockHash), Is.Null);
    }

    [Test]
    public void Same_hash_at_different_numbers_does_not_collide()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        Hash256 blockHash = TestItem.KeccakA;
        byte[] balLow = [0xc1, 0x01];
        byte[] balHigh = [0xc1, 0x02];

        store.Insert(1, blockHash, balLow);
        store.Insert(2, blockHash, balHigh);

        using MemoryManager<byte>? rlpLow = store.GetRlp(1, blockHash);
        using MemoryManager<byte>? rlpHigh = store.GetRlp(2, blockHash);

        Assert.That(rlpLow!.Memory, Is.SequenceEqualTo(balLow));
        Assert.That(rlpHigh!.Memory, Is.SequenceEqualTo(balHigh));

        store.Delete(1, blockHash);
        Assert.That(store.Exists(1, blockHash), Is.False);
        Assert.That(store.Exists(2, blockHash), Is.True);
    }

    [Test]
    public void Stores_under_block_number_prefixed_key()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 7;
        Hash256 blockHash = TestItem.KeccakA;
        byte[] encoded = [0xc1, 0x80];

        store.Insert(blockNumber, blockHash, encoded);

        byte[] expectedKey = Bytes.Concat(blockNumber.ToBigEndianByteArray(), blockHash.BytesToArray());
        Assert.That(db[expectedKey], Is.EqualTo(encoded));
        Assert.That(db[blockHash.Bytes], Is.Null);
    }

    [Test]
    public async Task Deferred_insert_is_visible_before_flush_and_frees_the_live_bal()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        BlockAccessListStore store = new(db, null, writer);

        byte[] bal = [0xc1, 0x80];
        Block block = BlockWithBal(5, bal);
        store.InsertFromBlockDeferred(block);

        Assert.That(block.EncodedBlockAccessList, Is.Null, "live BAL freed synchronously");
        Assert.That(store.Exists(block.Number, block.Hash!), Is.True, "served from the overlay before flush");
        using (MemoryManager<byte>? pending = store.GetRlp(block.Number, block.Hash!))
        {
            Assert.That(pending!.Memory, Is.SequenceEqualTo(bal));
        }

        Assert.That(new BlockAccessListStore(db).Exists(block.Number, block.Hash!), Is.False, "not durable until flush");

        writer.Pump();

        Assert.That(ReadDurable(db, block), Is.EqualTo(bal));
    }

    [Test]
    public async Task Decoded_bal_is_not_written_again_when_the_store_holds_it([Values] bool deferred)
    {
        using TestMemDb db = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        using IContainer container = CreateStoreContainer(db, writer);
        IBlockAccessListStore store = container.Resolve<IBlockAccessListStore>();
        byte[] stored = [0xc1, 0x80];
        Block block = Build.A.Block.WithNumber(5).WithBlockAccessList(new ReadOnlyBlockAccessList()).TestObject;
        byte[] key = Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray());
        store.Insert(block.Number, block.Hash!, stored);
        Assert.That(db[key], Is.EqualTo(stored), "The existing encoding must be durable before insertion.");

        if (deferred)
        {
            store.InsertFromBlockDeferred(block);
        }
        else
        {
            store.InsertFromBlock(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.QueuedCount, Is.Zero);
            Assert.That(db[key], Is.EqualTo(stored));
        }
    }

    [Test]
    public async Task Decoded_bal_is_written_when_the_store_does_not_hold_it([Values] bool deferred)
    {
        using TestMemDb db = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        using IContainer container = CreateStoreContainer(db, writer);
        IBlockAccessListStore store = container.Resolve<IBlockAccessListStore>();
        ReadOnlyBlockAccessList bal = new();
        Block block = Build.A.Block.WithNumber(5).WithBlockAccessList(bal).TestObject;
        byte[] key = Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray());
        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(bal);
        Assert.That(store.Exists(block.Number, block.Hash!), Is.False, "The block must have no stored or pending BAL before insertion.");

        if (deferred)
        {
            store.InsertFromBlockDeferred(block);
        }
        else
        {
            store.InsertFromBlock(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.QueuedCount, Is.EqualTo(deferred ? 1 : 0));
            Assert.That(db[key], Is.EqualTo(deferred ? null : encoded),
                "The durable database must match the insertion mode before flush.");
        }

        using (MemoryManager<byte>? pending = store.GetRlp(block.Number, block.Hash!))
        {
            Assert.That(pending, Is.Not.Null, "The inserted BAL must be readable before flush.");
            Assert.That(pending!.Memory, Is.SequenceEqualTo(encoded));
        }

        writer.Pump();

        Assert.That(db[key], Is.EqualTo(encoded));
    }

    [Test]
    public async Task Replacing_pending_bal_coalesces_the_queued_write()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        BlockAccessListStore store = new(db, null, writer);

        Block block = BlockWithBal(5, [0xc1, 0x01]);
        store.InsertFromBlockDeferred(block);

        byte[] authoritativeBal = [0xc1, 0x02];
        block.EncodedBlockAccessList = authoritativeBal;
        store.InsertFromBlockDeferred(block);

        Assert.That(writer.QueuedCount, Is.EqualTo(1), "the replacement should reuse the existing queued operation");

        writer.Pump();

        Assert.That(ReadDurable(db, block), Is.EqualTo(authoritativeBal));
    }

    [Test]
    public async Task Barrier_flush_drains_queued_bal_and_fsyncs_before_state_persists()
    {
        TestMemDb db = new();
        StatePersistenceBarrier barrier = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter(barrier);
        BlockAccessListStore store = new(db, null, writer, persistenceBarrier: barrier);

        byte[] balA = [0xc1, 0x01];
        byte[] balB = [0xc1, 0x02];
        Block a = BlockWithBal(5, balA);
        Block b = BlockWithBal(6, balB);
        store.InsertFromBlockDeferred(a);
        store.InsertFromBlockDeferred(b);

        Assert.That(new BlockAccessListStore(db).Exists(a.Number, a.Hash!), Is.False, "not durable until the barrier flushes");

        barrier.FlushDeferred();

        Assert.That(ReadDurable(db, a), Is.EqualTo(balA));
        Assert.That(ReadDurable(db, b), Is.EqualTo(balB));
        Assert.That(db.FlushCount, Is.GreaterThan(0), "BAL WAL was fsynced before state persist");
    }

    [Test]
    public async Task Disabled_writer_inserts_bal_synchronously()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter disabled = DeferredWriteTestHelpers.DisabledWriter();
        BlockAccessListStore store = new(db, null, disabled);

        byte[] bal = [0xc1, 0x80];
        Block block = BlockWithBal(1, bal);
        store.InsertFromBlockDeferred(block);

        Assert.That(ReadDurable(db, block), Is.EqualTo(bal));
    }

    [Test]
    public async Task Range_deletion_releases_the_sync_gate_before_reclaim([Values] bool deferred)
    {
        using RangeTrackingDb db = new();
        await using DeferredBlockDataWriter writer = deferred ? DeferredWriteTestHelpers.ManualWriter() : DeferredWriteTestHelpers.DisabledWriter();
        ContainerBuilder builder = new();
        builder.RegisterModule(new TestNethermindModule());
        builder.RegisterInstance(db).Keyed<IDb>(DbNames.BlockAccessLists);
        builder.RegisterInstance(writer).As<IDeferredBlockDataWriter>().ExternallyOwned();
        using IContainer container = builder.Build();
        IBlockAccessListStore store = container.Resolve<IBlockAccessListStore>();
        Block oldBlock = BlockWithBal(5, [0xc0]);
        store.InsertFromBlockDeferred(oldBlock);

        bool deletedUnderLock = false;
        db.Removing = () => deletedUnderLock = store.WriteLock.IsHeldByCurrentThread;
        TaskCompletionSource reclaimStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim finishReclaim = new();
        db.Reclaiming = () =>
        {
            reclaimStarted.SetResult();
            if (!finishReclaim.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };

        Task deletion = Task.Run(() => store.DeleteRange(1, 6));
        Task? insertion = null;
        try
        {
            await reclaimStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            insertion = Task.Run(() =>
            {
                lock (store.WriteLock)
                {
                    store.Insert(6, TestItem.KeccakB, new byte[] { 0xc0 });
                }
            });
            await insertion.WaitAsync(TimeSpan.FromSeconds(10));
            writer.Pump();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deletedUnderLock, Is.True);
                Assert.That(store.Exists(oldBlock.Number, oldBlock.Hash!), Is.False);
                Assert.That(store.Exists(6, TestItem.KeccakB), Is.True);
                Assert.That(deletion.IsCompleted, Is.False, "reclaim is still blocked while sync inserts retained data");
            }
        }
        finally
        {
            finishReclaim.Set();
            await deletion.WaitAsync(TimeSpan.FromSeconds(10));
            if (insertion is not null) await insertion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static IContainer CreateStoreContainer(TestMemDb db, DeferredBlockDataWriter writer)
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new TestNethermindModule());
        builder.RegisterInstance(db).Keyed<IDb>(DbNames.BlockAccessLists).ExternallyOwned();
        builder.RegisterInstance(writer).As<IDeferredBlockDataWriter>().ExternallyOwned();
        return builder.Build();
    }

    private sealed class RangeTrackingDb : TestMemDb, IRangeRemovableKeyValueStore
    {
        public Action? Removing { get; set; }
        public Action? Reclaiming { get; set; }

        void IRangeRemovableKeyValueStore.RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        {
            Removing?.Invoke();
            base.RemoveRange(firstKeyInclusive, lastKeyExclusive);
        }

        void IRangeRemovableKeyValueStore.ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) => Reclaiming?.Invoke();
    }

    private static Block BlockWithBal(ulong number, byte[] encodedBal)
    {
        Block block = Build.A.Block.WithNumber(number).TestObject;
        block.EncodedBlockAccessList = encodedBal;
        return block;
    }

    private static byte[]? ReadDurable(TestMemDb db, Block block)
    {
        using MemoryManager<byte>? rlp = new BlockAccessListStore(db).GetRlp(block.Number, block.Hash!);
        return rlp?.Memory.ToArray();
    }
}
