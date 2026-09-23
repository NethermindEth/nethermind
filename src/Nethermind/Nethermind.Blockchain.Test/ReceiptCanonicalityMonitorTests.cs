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

        receiptStorage.NewCanonicalReceipts += Raise.EventWith(new object(), new BlockReplacementEventArgs(first, removed));
        receiptStorage.NewCanonicalReceipts += Raise.EventWith(new object(), new BlockReplacementEventArgs(second));

        Assert.That(allPublished.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(published, Is.EqualTo(new[] { (removed.Hash, true), (first.Hash, false), (second.Hash, false) }));
    }
}
