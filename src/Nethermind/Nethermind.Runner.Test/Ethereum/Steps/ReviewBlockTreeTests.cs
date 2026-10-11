// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Consensus.Processing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class ReviewBlockTreeTests
{
    [Test]
    public async Task Repeated_queue_empty_events_do_not_throw_into_the_processor()
    {
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.ProcessingEnabled.Returns(true);
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        queue.IsEmpty.Returns(false);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Accept(Arg.Any<IBlockTreeVisitor>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        ReviewBlockTree step = new(
            Substitute.For<IWorldStateManager>(),
            initConfig,
            new SyncConfig(),
            queue,
            blockTree,
            Substitute.For<IBlockTreeHealer>(),
            LimboLogs.Instance);

        // Continuations of the step are captured by this context and run only when drained, so the step
        // cannot unsubscribe between the two raises below.
        QueuedSynchronizationContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task execute;
        try
        {
            execute = step.Execute(CancellationToken.None);
            context.Drain();
            Assert.That(execute.IsCompleted, Is.False);

            // BlockchainProcessor raises ProcessingQueueEmpty after every dequeued block and every batch, so the
            // second raise can arrive before the step's continuation has unsubscribed; it must not throw into the loop.
            queue.ProcessingQueueEmpty += Raise.Event<EventHandler>(queue, EventArgs.Empty);
            Assert.DoesNotThrow(() => queue.ProcessingQueueEmpty += Raise.Event<EventHandler>(queue, EventArgs.Empty));
            context.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await execute.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void Drain()
        {
            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
            {
                item.Callback(item.State);
            }
        }
    }
}
