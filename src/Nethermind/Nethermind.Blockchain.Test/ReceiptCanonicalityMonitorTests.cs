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
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, LimboLogs.Instance);

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
    public void Blocked_subscriber_does_not_delay_another()
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(Arg.Any<Block>()).Returns([]);
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, LimboLogs.Instance);

        using CountdownEvent otherReceivedBoth = new(2);
        using ManualResetEventSlim unblockedByOther = new();
        monitor.ReceiptsInserted += (_, _) =>
        {
            if (otherReceivedBoth.Wait(Timeout)) unblockedByOther.Set();
        };
        monitor.ReceiptsInserted += (_, _) => otherReceivedBoth.Signal();

        RaiseNewCanonical(receiptStorage, Build.A.Block.WithNumber(1).TestObject);
        RaiseNewCanonical(receiptStorage, Build.A.Block.WithNumber(2).TestObject);

        Assert.That(unblockedByOther.Wait(Timeout), Is.True);
    }

    [Test]
    public void Drops_events_still_queued_for_an_unsubscribed_handler()
    {
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(Arg.Any<Block>()).Returns([]);
        using ReceiptCanonicalityMonitor monitor = new(receiptStorage, LimboLogs.Instance);

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
        // Every subscriber's queue gets an event before any is delivered, so this proves block 2 is queued for the handler.
        using ManualResetEventSlim secondQueued = new();
        monitor.ReceiptsInserted += (_, e) =>
        {
            if (e.BlockHeader.Number == 2) secondQueued.Set();
        };

        RaiseNewCanonical(receiptStorage, Build.A.Block.WithNumber(1).TestObject);
        Assert.That(firstEntered.Wait(Timeout), Is.True);
        RaiseNewCanonical(receiptStorage, Build.A.Block.WithNumber(2).TestObject);
        Assert.That(secondQueued.Wait(Timeout), Is.True);

        monitor.ReceiptsInserted -= handler;
        release.Set();

        // Had block 2's event not been dropped, it would be delivered right after block 1's handler returns.
        Assert.That(secondEntered.Wait(TimeSpan.FromMilliseconds(200)), Is.False);
    }

    private static void RaiseNewCanonical(IReceiptStorage receiptStorage, Block block, Block? previous = null) =>
        receiptStorage.NewCanonicalReceipts += Raise.EventWith(new object(), new BlockReplacementEventArgs(block, previous));
}
