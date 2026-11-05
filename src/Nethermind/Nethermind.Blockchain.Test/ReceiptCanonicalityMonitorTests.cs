// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class ReceiptCanonicalityMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public void Publishes_receipts_in_canonicalisation_order()
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, Substitute.For<IBlockTree>(), LimboLogs.Instance, MainnetSpecProvider.Instance);

        Block removed = Build.A.Block.WithNumber(1).WithExtraData([1]).TestObject;
        Block first = Build.A.Block.WithNumber(1).TestObject;
        Block second = Build.A.Block.WithNumber(2).TestObject;

        // Holds the first dispatch until the second block's receipts are read, which an unordered dispatch does at once.
        using ManualResetEventSlim secondRead = new();
        receiptStorage.Get(removed).Returns(_ =>
        {
            secondRead.Wait(TimeSpan.FromSeconds(1));
            return [];
        });
        receiptStorage.Get(first).Returns([]);
        receiptStorage.Get(second).Returns(_ =>
        {
            secondRead.Set();
            return [];
        });

        ConcurrentQueue<(Hash256?, bool, Transaction[]?)> published = new();
        using CountdownEvent allPublished = new(3);
        monitor.ReceiptsInserted += (_, e) =>
        {
            published.Enqueue((e.BlockHeader.Hash, e.WasRemoved, e.Transactions));
            allPublished.Signal();
        };

        RaiseNewCanonical(receiptStorage, first, removed);
        RaiseNewCanonical(receiptStorage, second);

#pragma warning disable NUnit2045 // Completion must be confirmed before inspecting or advancing the operation.
        Assert.That(allPublished.Wait(Timeout), Is.True);
#pragma warning restore NUnit2045
        Assert.That(published, Is.EqualTo(new[] { (removed.Hash, true, removed.Transactions), (first.Hash, false, first.Transactions), (second.Hash, false, second.Transactions) }));
    }

    [Test]
    public void Publishes_new_block_when_reading_the_removed_block_fails()
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, Substitute.For<IBlockTree>(), LimboLogs.Instance, MainnetSpecProvider.Instance);

        Block removed = Build.A.Block.WithNumber(1).WithExtraData([1]).TestObject;
        Block added = Build.A.Block.WithNumber(1).TestObject;
        receiptStorage.Get(removed).Returns(_ => throw new InvalidOperationException());
        receiptStorage.Get(added).Returns([]);

        ConcurrentQueue<(Hash256?, bool)> published = new();
        using ManualResetEventSlim addedPublished = new();
        monitor.ReceiptsInserted += (_, e) =>
        {
            published.Enqueue((e.BlockHeader.Hash, e.WasRemoved));
            if (e.BlockHeader.Hash == added.Hash) addedPublished.Set();
        };

        RaiseNewCanonical(receiptStorage, added, removed);

#pragma warning disable NUnit2045 // Completion must be confirmed before inspecting or advancing the operation.
        Assert.That(addedPublished.Wait(Timeout), Is.True);
#pragma warning restore NUnit2045
        Assert.That(published, Is.EqualTo(new[] { (added.Hash, false) }));
    }

    [Test]
    public void Publishes_block_removed_from_main_before_the_following_update()
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(Arg.Any<Block>()).Returns([]);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, blockTree, LimboLogs.Instance, MainnetSpecProvider.Instance);

        Block removed = Build.A.Block.WithNumber(2).WithExtraData([1]).TestObject;
        blockTree.FindBlock(removed.Hash!, Arg.Any<BlockTreeLookupOptions>(), removed.Number).Returns(removed);
        Block added = Build.A.Block.WithNumber(2).TestObject;

        ConcurrentQueue<(Hash256?, bool)> published = new();
        using CountdownEvent allPublished = new(2);
        monitor.ReceiptsInserted += (_, e) =>
        {
            published.Enqueue((e.BlockHeader.Hash, e.WasRemoved));
            allPublished.Signal();
        };

        blockTree.BlockRemovedFromMain += Raise.EventWith(new object(), new BlockHeaderEventArgs(removed.Header));
        RaiseNewCanonical(receiptStorage, added);

#pragma warning disable NUnit2045 // Completion must be confirmed before inspecting or advancing the operation.
        Assert.That(allPublished.Wait(Timeout), Is.True);
#pragma warning restore NUnit2045
        Assert.That(published, Is.EqualTo(new[] { (removed.Hash, true), (added.Hash, false) }));
    }

    [Test]
    public void Drops_events_still_queued_for_an_unsubscribed_handler([Values] bool disposeMonitor)
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(Arg.Any<Block>()).Returns([]);
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, Substitute.For<IBlockTree>(), LimboLogs.Instance, MainnetSpecProvider.Instance);

        using ManualResetEventSlim firstEntered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim secondEntered = new();
        EventHandler<ReceiptsEventArgs> handler = (_, _) =>
        {
            if (firstEntered.IsSet) secondEntered.Set();
            firstEntered.Set();
            release.Wait(Timeout);
        };
        monitor.ReceiptsInserted += handler;

        RaiseNewCanonical(receiptStorage, Build.A.Block.WithNumber(1).TestObject);
        Assert.That(firstEntered.Wait(Timeout), Is.True);
        // Queued behind block 1, whose handler is still running.
        RaiseNewCanonical(receiptStorage, Build.A.Block.WithNumber(2).TestObject);

        if (disposeMonitor)
        {
            monitor.Dispose();
        }
        else
        {
            monitor.ReceiptsInserted -= handler;
        }

        release.Set();

        // Had block 2's event not been dropped, it would be delivered right after block 1's handler returns.
        Assert.That(secondEntered.Wait(TimeSpan.FromMilliseconds(200)), Is.False);
    }

    [Test]
    public void Transient_notifications_survive_cleanup_while_delivery_is_queued([Values] bool evict, [Values] bool removal)
    {
        using TransientReceiptTestContext ctx = new();
        IReceiptMonitor monitor = ctx.Container.Resolve<IReceiptMonitor>();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim delivered = new();
        (Block first, TxReceipt firstReceipt) = TransientReceiptTestContext.Sample(1, 100_000);
        (Block second, TxReceipt receipt) = TransientReceiptTestContext.Sample(2, 100_000, parent: first);
        TxReceipt[]? received = null;
        monitor.ReceiptsInserted += (_, e) =>
        {
            if (e.BlockHeader.Hash == first.Hash && !e.WasRemoved)
            {
                entered.Set();
                release.Wait(Timeout);
            }
            else if (e.BlockHeader.Hash == second.Hash && e.WasRemoved == removal)
            {
                received = e.TxReceipts;
                delivered.Set();
            }
        };
        ctx.Storage.Insert(first, [firstReceipt], ensureCanonical: false);
        ctx.Canonicalize(first);
        try
        {
            Assert.That(entered.Wait(Timeout), Is.True);
            ctx.Storage.Insert(second, [receipt], ensureCanonical: false);
            ctx.Canonicalize(second);
            if (removal) Assert.That(ctx.Tree.TryRewindHead(first.Hash!), Is.True);
            if (evict)
            {
                (Block large, TxReceipt largeReceipt) = TransientReceiptTestContext.Sample(3, 980_000);
                ctx.Storage.Insert(large, [largeReceipt], ensureCanonical: false);
            }
            else
                ctx.Tree.ForkChoiceUpdated(second.Hash, second.Hash);
            Assert.That(ctx.Storage.Get(second), Is.Empty);
        }
        finally
        {
            release.Set();
        }
        Assert.That(delivered.Wait(Timeout), Is.True);
        Assert.That(received, Is.EqualTo(new[] { receipt }));
    }

    [Test]
    public void Transient_delivery_budget_bounds_a_slow_subscriber_and_recovers_after_drain([Values] bool transactionPayload)
    {
        using TransientReceiptTestContext ctx = new();
        IReceiptMonitor monitor = ctx.Container.Resolve<IReceiptMonitor>();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim drained = new();
        using ManualResetEventSlim delivered = new();
        int logBytes = transactionPayload ? 0 : 900_000;
        int transactionBytes = transactionPayload ? 200_000 : 0;
        (Block first, TxReceipt firstReceipt) = TransientReceiptTestContext.Sample(1, logBytes, transactionBytes: transactionBytes);
        (Block second, TxReceipt secondReceipt) = TransientReceiptTestContext.Sample(2, logBytes, parent: first, transactionBytes: transactionBytes);
        (Block third, TxReceipt thirdReceipt) = TransientReceiptTestContext.Sample(3, logBytes, parent: second, transactionBytes: transactionBytes);
        Block sentinel = Build.A.Block.WithParent(third).WithNumber(4).TestObject;
        (Block next, TxReceipt nextReceipt) = TransientReceiptTestContext.Sample(5, logBytes, parent: sentinel, transactionBytes: transactionBytes);
        int skippedDelivered = 0;
        monitor.ReceiptsInserted += (_, e) =>
        {
            if (e.BlockHeader.Hash == first.Hash)
            {
                entered.Set();
                release.Wait(Timeout);
            }
            else if (e.BlockHeader.Hash == sentinel.Hash) drained.Set();
            else if (e.BlockHeader.Hash == second.Hash || e.BlockHeader.Hash == third.Hash)
                Interlocked.Increment(ref skippedDelivered);
            else if (e.BlockHeader.Hash == next.Hash) delivered.Set();
        };
        long before = Interlocked.Read(ref Metrics.TransientReceiptNotificationsDropped);
        ctx.Storage.Insert(first, [firstReceipt], ensureCanonical: false);
        ctx.Canonicalize(first);
        try
        {
            Assert.That(entered.Wait(Timeout), Is.True);
            ctx.Storage.Insert(second, [secondReceipt], ensureCanonical: false);
            ctx.Canonicalize(second);
            ctx.Storage.Insert(third, [thirdReceipt], ensureCanonical: false);
            ctx.Canonicalize(third);
            ctx.Storage.Insert(sentinel, [], ensureCanonical: false);
            ctx.Canonicalize(sentinel);
        }
        finally
        {
            release.Set();
        }
        Assert.That(drained.Wait(Timeout), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Volatile.Read(ref skippedDelivered), Is.Zero);
            Assert.That(Interlocked.Read(ref Metrics.TransientReceiptNotificationsDropped), Is.GreaterThanOrEqualTo(before + 2));
        }
        ctx.Storage.Insert(next, [nextReceipt], ensureCanonical: false);
        ctx.Canonicalize(next);
        Assert.That(delivered.Wait(Timeout), Is.True);
    }

    private static void RaiseNewCanonical(IReceiptStorage receiptStorage, Block block, Block? previous = null) =>
        receiptStorage.NewCanonicalReceipts += Raise.EventWith(new object(), new BlockReplacementEventArgs(block, previous));
}
