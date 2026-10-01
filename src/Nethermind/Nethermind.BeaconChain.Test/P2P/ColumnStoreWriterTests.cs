// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class ColumnStoreWriterTests
{
    /// <summary>Writes run in the order they were posted, and disposal waits for every one queued before it, so none outlives the store.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task Disposal_drains_the_queued_writes_in_order_and_later_ones_never_run(CancellationToken token)
    {
        ConcurrentQueue<int> ran = [];
        using ManualResetEventSlim release = new();
        ColumnStoreWriter writer = new(LimboLogs.Instance);
        writer.Post(() => release.Wait(token));
        for (int i = 0; i < 3; i++)
        {
            int write = i;
            writer.Post(() => ran.Enqueue(write));
        }

        Task disposed = Task.Run(writer.Dispose, token);
        Assert.That(SpinWait.SpinUntil(() => disposed.IsCompleted, TimeSpan.FromMilliseconds(200)), Is.False, "disposal returned with writes still queued");
        release.Set();
        await disposed.WaitAsync(token);
        writer.Post(() => ran.Enqueue(100));

        Assert.That(ran, Is.EqualTo(new[] { 0, 1, 2 }));
    }
}
