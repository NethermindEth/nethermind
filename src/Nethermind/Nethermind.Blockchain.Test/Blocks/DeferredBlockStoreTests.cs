// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class DeferredBlockStoreTests
{
    [Test]
    public async Task Deferred_insert_is_visible_before_flush_through_all_read_paths()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter writer = new(enabled: true, capacity: 8, LimboLogs.Instance, startConsumer: false);
        BlockStore store = new(db, null, writer);

        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        store.InsertDeferred(block);

        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.True);

        // Served from the overlay as a fresh decode (store fidelity - senders are recovered on
        // demand exactly as for a database read), so assert identity by hash and shape.
        Block? served = store.Get(block.Number, block.Hash!);
        Assert.That(served, Is.Not.Null);
        Assert.That(served!.Hash, Is.EqualTo(block.Hash));
        Assert.That(served.Transactions.Length, Is.EqualTo(1));
        Assert.That(store.GetRlp(block.Number, block.Hash!), Is.Not.Null);

        ReceiptRecoveryBlock? recovery = store.GetReceiptRecoveryBlock(block.Number, block.Hash!);
        Assert.That(recovery, Is.Not.Null);
        Assert.That(recovery!.Value.TransactionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Deferred_get_returns_a_fresh_instance_not_the_live_block()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter writer = new(enabled: true, capacity: 8, LimboLogs.Instance, startConsumer: false);
        BlockStore store = new(db, null, writer);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.InsertDeferred(block);

        // The overlay holds encoded bytes, so a read decodes a distinct instance - the caller can
        // never mutate the live block through a pending read.
        Assert.That(store.Get(block.Number, block.Hash!), Is.Not.SameAs(block));
        Assert.That(store.Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
    }

    [Test]
    public async Task Deferred_insert_is_durable_and_byte_identical_after_flush()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter writer = new(enabled: true, capacity: 8, LimboLogs.Instance, startConsumer: false);
        BlockStore store = new(db, null, writer);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.InsertDeferred(block);
        byte[]? pendingRlp = store.GetRlp(block.Number, block.Hash!);

        writer.Pump();

        // A fresh store over the same database proves durability; bytes must match the pending view.
        BlockStore reopened = new(db);
        Assert.That(reopened.Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
        Assert.That(reopened.GetRlp(block.Number, block.Hash!), Is.EqualTo(pendingRlp));
    }

    [Test]
    public async Task Deleted_block_is_not_resurrected_by_queued_write()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter writer = new(enabled: true, capacity: 8, LimboLogs.Instance, startConsumer: false);
        BlockStore store = new(db, null, writer);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.InsertDeferred(block);
        store.Delete(block.Number, block.Hash!);

        Assert.That(store.Get(block.Number, block.Hash!), Is.Null, "deleted block must not be readable before flush");

        writer.Pump();

        Assert.That(store.Get(block.Number, block.Hash!), Is.Null, "a queued insert must not resurrect a deleted block");
        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.False);
    }

    [Test]
    public async Task Disabled_writer_inserts_synchronously()
    {
        TestMemDb db = new();
        await using DeferredBlockDataWriter disabled = new(enabled: false, capacity: 8, LimboLogs.Instance, startConsumer: false);
        BlockStore store = new(db, null, disabled);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.InsertDeferred(block);

        BlockStore reopened = new(db);
        Assert.That(reopened.Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
    }
}
