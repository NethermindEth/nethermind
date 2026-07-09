// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeferredBlockStoreTests
{
    private readonly TestMemDb _db = new();
    private readonly StatePersistenceBarrier _barrier = new();
    private DeferredBlockDataWriter _writer = null!;
    private BlockStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _writer = DeferredWriteTestHelpers.ManualWriter(_barrier);
        _store = new BlockStore(_db, null, _writer, deferBodies: true, persistenceBarrier: _barrier);
    }

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

        // Served from the pending snapshot (a distinct instance from the live block), so assert hash + shape.
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

        // The overlay holds a sanitized header+body snapshot, so a read serves a distinct instance, never the live block.
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
    public void Barrier_flush_drains_queued_bodies_and_fsyncs_before_state_persists()
    {
        Block a = BlockNumbered(5);
        Block b = BlockNumbered(6);
        _store.InsertDeferred(a);
        _store.InsertDeferred(b);

        // The consumer is not running, so only the barrier (via the writer's drain) makes these durable.
        Assert.That(Reopen().Get(a.Number, a.Hash!), Is.Null, "not durable until the barrier flushes");

        _barrier.FlushDeferred();

        Assert.That(Reopen().Get(a.Number, a.Hash!), Is.EqualTo(a).UsingBlockComparer());
        Assert.That(Reopen().Get(b.Number, b.Hash!), Is.EqualTo(b).UsingBlockComparer());
        Assert.That(_db.FlushCount, Is.GreaterThan(0), "blocks WAL was fsynced before state persist");
    }

    [Test]
    public void Deferred_insert_with_pre_encoded_transactions_is_byte_identical_to_from_scratch()
    {
        Transaction[] txs =
        [
            Build.A.Transaction.WithNonce(1).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(TxType.EIP1559).Signed().TestObject,
        ];
        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(1).WithTransactions(txs).TestObject;

        byte[] fromScratch = new BlockDecoder().Encode(block).Bytes;

        // Mirror the newPayload path: the block arrives with pre-encoded transactions the snapshot must carry
        // through the deferred write without changing the persisted bytes.
        block.EncodedTransactions = Array.ConvertAll(txs, static tx => Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes);

        _store.InsertDeferred(block);
        _writer.Pump();

        Assert.That(Reopen().GetRlp(block.Number, block.Hash!), Is.EqualTo(fromScratch));
    }

    [Test]
    public async Task Disabled_writer_inserts_synchronously()
    {
        await using DeferredBlockDataWriter disabled = DeferredWriteTestHelpers.DisabledWriter();
        BlockStore store = new(_db, null, disabled, deferBodies: true);

        Block block = BlockNumbered(1);
        store.InsertDeferred(block);

        Assert.That(Reopen().Get(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
    }
}
