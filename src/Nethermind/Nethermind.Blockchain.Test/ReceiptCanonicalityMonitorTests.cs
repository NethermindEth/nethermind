// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Blockchain.Receipts;
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

        ConcurrentQueue<(Hash256?, bool)> published = new();
        using CountdownEvent allPublished = new(3);
        monitor.ReceiptsInserted += (_, e) =>
        {
            published.Enqueue((e.BlockHeader.Hash, e.WasRemoved));
            allPublished.Signal();
        };

        RaiseNewCanonical(receiptStorage, first, removed);
        RaiseNewCanonical(receiptStorage, second);

        Assert.That(allPublished.Wait(Timeout), Is.True);
        Assert.That(published, Is.EqualTo(new[] { (removed.Hash, true), (first.Hash, false), (second.Hash, false) }));
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

        Assert.That(addedPublished.Wait(Timeout), Is.True);
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

        Assert.That(allPublished.Wait(Timeout), Is.True);
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
        IBlockTree tree = Substitute.For<IBlockTree>();
        ReceiptConfig config = new() { StoreReceipts = false, MaxTransientReceiptStorageSize = 1 };
        using TransientReceiptStorage storage = new(tree, Substitute.For<IReceiptsRecovery>(), config, LimboLogs.Instance);
        using ReceiptCanonicalityMonitor monitor = new(storage, tree, LimboLogs.Instance, MainnetSpecProvider.Instance, config);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim delivered = new();
        Block first = Build.A.Block.WithNumber(1).TestObject;
        Block second = Build.A.Block.WithNumber(2).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, new byte[100_000], [])).TestObject;
        TxReceipt[]? received = null;
        monitor.ReceiptsInserted += (_, e) =>
        {
            if (e.BlockHeader.Hash == first.Hash)
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
        storage.Insert(first, [Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, new byte[100_000], [])).TestObject]);
        tree.BlockAddedToMain += Raise.EventWith(tree, new BlockReplacementEventArgs(first));
        try
        {
            Assert.That(entered.Wait(Timeout), Is.True);
            storage.Insert(second, [receipt]);
            if (removal)
                tree.BlockRemovedFromMain += Raise.EventWith(tree, new BlockHeaderEventArgs(second.Header));
            else
                tree.BlockAddedToMain += Raise.EventWith(tree, new BlockReplacementEventArgs(second));
            if (evict)
                storage.Insert(Build.A.Block.WithNumber(3).TestObject,
                    [Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, new byte[980_000], [])).TestObject]);
            else
                tree.BlocksFinalized += Raise.EventWith(tree, new FinalizeEventArgs(second.Header));
            Assert.That(storage.Get(second), Is.Empty);
        }
        finally
        {
            release.Set();
        }
        Assert.That(delivered.Wait(Timeout), Is.True);
        Assert.That(received, Is.EqualTo(new[] { receipt }));
    }

    [Test]
    public void Transient_delivery_budget_bounds_a_slow_subscriber_and_recovers_after_drain()
    {
        IBlockTree tree = Substitute.For<IBlockTree>();
        ReceiptConfig config = new() { StoreReceipts = false, MaxTransientReceiptStorageSize = 1 };
        using TransientReceiptStorage storage = new(tree, Substitute.For<IReceiptsRecovery>(), config, LimboLogs.Instance);
        using ReceiptCanonicalityMonitor monitor = new(storage, tree, LimboLogs.Instance, MainnetSpecProvider.Instance, config);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim drained = new();
        using ManualResetEventSlim delivered = new();
        Block first = Build.A.Block.WithNumber(1).TestObject;
        Block second = Build.A.Block.WithNumber(2).TestObject;
        Block sentinel = Build.A.Block.WithNumber(3).TestObject;
        int count = 0;
        monitor.ReceiptsInserted += (_, e) =>
        {
            if (e.BlockHeader.Hash == first.Hash)
            {
                entered.Set();
                release.Wait(Timeout);
            }
            else if (e.BlockHeader.Hash == sentinel.Hash) drained.Set();
            else if (e.BlockHeader.Hash == second.Hash)
            {
                Interlocked.Increment(ref count);
                delivered.Set();
            }
        };
        static TxReceipt[] LargeReceipts() => [Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, new byte[900_000], [])).TestObject];
        storage.Insert(first, LargeReceipts());
        tree.BlockAddedToMain += Raise.EventWith(tree, new BlockReplacementEventArgs(first));
        try
        {
            Assert.That(entered.Wait(Timeout), Is.True);
            storage.Insert(second, LargeReceipts());
            tree.BlockAddedToMain += Raise.EventWith(tree, new BlockReplacementEventArgs(second));
            storage.Insert(sentinel, []);
            tree.BlockAddedToMain += Raise.EventWith(tree, new BlockReplacementEventArgs(sentinel));
        }
        finally
        {
            release.Set();
        }
        Assert.That(drained.Wait(Timeout), Is.True);
        Assert.That(Volatile.Read(ref count), Is.Zero, "the second large event exceeded the pending delivery budget");
        tree.BlockAddedToMain += Raise.EventWith(tree, new BlockReplacementEventArgs(second));
        Assert.That(delivered.Wait(Timeout), Is.True);
        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    private static void RaiseNewCanonical(IReceiptStorage receiptStorage, Block block, Block? previous = null) =>
        receiptStorage.NewCanonicalReceipts += Raise.EventWith(new object(), new BlockReplacementEventArgs(block, previous));
}
