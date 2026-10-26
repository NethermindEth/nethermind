// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.BeaconChain.P2P;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.P2P;

[CancelAfter(60_000)]
public class ColumnStoreWriterTests
{
    [Test]
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

    [Test]
    public async Task A_barrier_requested_while_disposal_drains_completes_only_after_the_running_write(CancellationToken token)
    {
        using ManualResetEventSlim running = new();
        using ManualResetEventSlim release = new();
        bool writeFinished = false;
        ColumnStoreWriter writer = new(LimboLogs.Instance);
        writer.Post(() =>
        {
            running.Set();
            release.Wait(token);
            Volatile.Write(ref writeFinished, true);
        });
        Assert.That(running.Wait(TimeSpan.FromSeconds(30), token), Is.True, "fixture");

        Task disposed = Task.Run(writer.Dispose, token);
        Task previous = writer.WhenWritten();
        Task barrier;
        while (!ReferenceEquals(barrier = writer.WhenWritten(), previous))
        {
            token.ThrowIfCancellationRequested();
            previous = barrier;
        }

        bool completedEarly = barrier.IsCompleted;
        release.Set();
        await barrier.WaitAsync(token);
        await disposed.WaitAsync(token);

        Assert.That(completedEarly, Is.False, "the barrier completed while a write was still running");
        Assert.That(Volatile.Read(ref writeFinished), Is.True);
    }
}
