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
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeferredBlockStoreTests
{
    private readonly TestMemDb _db = new();
    private readonly StatePersistenceBarrier _barrier = new();
    // startConsumer:false makes pre-flush states deterministic; Pump() / the barrier flush on demand.
    private readonly DeferredBlockDataWriter _writer = new(enabled: true, capacity: 8, LimboLogs.Instance, startConsumer: false);
    private BlockStore _store = null!;

    [SetUp]
    public void SetUp() => _store = new BlockStore(_db, null, _writer, deferBodies: true, persistenceBarrier: _barrier);

    [TearDown]
    public Task TearDown() => _writer.DisposeAsync().AsTask();

    // A fresh store over the same database, proving what is durable independent of the overlay.
    private BlockStore Reopen() => new(_db);

    private static Block BlockNumbered(ulong number) => Build.A.Block.WithNumber(number).TestObject;

    [Test]
    public void Deferred_insert_is_visible_before_flush_through_all_read_paths()
    {
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        _store.InsertDeferred(block);

        Assert.That(_store.HasBlock(block.Number, block.Hash!), Is.True);

        // Served as a fresh decode (senders recovered on demand as for a DB read), so assert hash + shape.
        Block? served = _store.Get(block.Number, block.Hash!);
        Assert.That(served, Is.Not.Null);
        Assert.That(served!.Hash, Is.EqualTo(block.Hash));
        Assert.That(served.Transactions.Length, Is.EqualTo(1));
        Assert.That(_store.GetRlp(block.Number, block.Hash!), Is.Not.Null);

        ReceiptRecoveryBlock? recovery = _store.GetReceiptRecoveryBlock(block.Number, block.Hash!);
        Assert.That(recovery, Is.Not.Null);
        Assert.That(recovery!.Value.TransactionCount, Is.EqualTo(1));
    }

    [Test]
    public void Deferred_get_returns_a_fresh_instance_not_the_live_block()
    {
        Block block = BlockNumbered(1);
        _store.InsertDeferred(block);

        // The overlay holds encoded bytes, so a read decodes a distinct instance the caller cannot mutate.
        Assert.That(_store.Get(block.Number, block.Hash!), Is.Not.SameAs(block));
        Assert.That(_store.Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
    }

    [Test]
    public void Deferred_insert_is_durable_and_byte_identical_after_flush()
    {
        Block block = BlockNumbered(1);
        _store.InsertDeferred(block);
        byte[]? pendingRlp = _store.GetRlp(block.Number, block.Hash!);

        _writer.Pump();

        Assert.That(Reopen().Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
        Assert.That(Reopen().GetRlp(block.Number, block.Hash!), Is.EqualTo(pendingRlp));
    }

    [Test]
    public void Deleted_block_is_not_resurrected_by_queued_write()
    {
        Block block = BlockNumbered(1);
        _store.InsertDeferred(block);
        _store.Delete(block.Number, block.Hash!);

        Assert.That(_store.Get(block.Number, block.Hash!), Is.Null, "deleted block must not be readable before flush");

        _writer.Pump();

        Assert.That(_store.Get(block.Number, block.Hash!), Is.Null, "a queued insert must not resurrect a deleted block");
        Assert.That(_store.HasBlock(block.Number, block.Hash!), Is.False);
    }

    [Test]
    public void Barrier_flush_makes_bodies_durable_up_to_the_watermark_and_leaves_the_rest_pending()
    {
        Block low = BlockNumbered(5);
        Block high = BlockNumbered(6);
        _store.InsertDeferred(low);
        _store.InsertDeferred(high);

        // The writer never drains, so only the barrier gate can persist.
        Assert.That(Reopen().Get(low.Number, low.Hash!), Is.Null, "not durable until the barrier flushes");

        _barrier.FlushBefore(5);

        Assert.That(Reopen().Get(low.Number, low.Hash!), Is.EqualTo(low).UsingBlockComparer(), "at-or-below watermark is durable");
        Assert.That(Reopen().Get(high.Number, high.Hash!), Is.Null, "above watermark stays pending");
        Assert.That(_store.HasBlock(high.Number, high.Hash!), Is.True, "still served from the overlay");
        Assert.That(_db.FlushCount, Is.GreaterThan(0), "blocks WAL was fsynced before state persist");
    }

    [Test]
    public async Task Disabled_writer_inserts_synchronously()
    {
        await using DeferredBlockDataWriter disabled = new(enabled: false, capacity: 8, LimboLogs.Instance, startConsumer: false);
        BlockStore store = new(_db, null, disabled, deferBodies: true);

        Block block = BlockNumbered(1);
        store.InsertDeferred(block);

        Assert.That(Reopen().Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
    }
}
