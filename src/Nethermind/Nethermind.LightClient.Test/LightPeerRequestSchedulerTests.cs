// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Logging;

namespace Nethermind.LightClient.Test;

public class LightPeerRequestSchedulerTests
{
    [Test]
    public async Task Shutdown_does_not_require_the_constructing_network_context_to_keep_running()
    {
        QueuedContext network = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        LightPeerRequestScheduler scheduler;
        try
        {
            SynchronizationContext.SetSynchronizationContext(network);
            scheduler = new(NullLogManager.Instance);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Task disposal = scheduler.DisposeAsync().AsTask();
        try
        {
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(network.Posts, Is.Zero);
        }
        finally
        {
            network.Drain();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        internal int Posts => _callbacks.Count;

        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));

        internal void Drain()
        {
            while (_callbacks.TryDequeue(out (SendOrPostCallback Callback, object? State) work))
                work.Callback(work.State);
        }
    }
}
