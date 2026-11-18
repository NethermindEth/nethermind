// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class LookupKNearestNeighbourTests
{
    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_prefer_verified_endpoint_over_a_third_party_mention(CancellationToken token)
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node seed = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
        Node known = new(TestItem.PublicKeyC, "192.168.1.3", 30303) { ValidatedProtocol = false };
        Node forged = new(TestItem.PublicKeyC, "192.168.1.4", 30303);
        IRoutingTable<Node, ValueHash256> routing = Substitute.For<IRoutingTable<Node, ValueHash256>>();
        routing.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<bool>()).Returns([seed]);
        routing.TryGet(known.Id.Hash.ValueHash256, out _).Returns(call =>
        {
            call[1] = known;
            return true;
        });
        INodeHealthTracker<Node> health = Substitute.For<INodeHealthTracker<Node>>();
        LookupKNearestNeighbour<PublicKey, Node, ValueHash256> lookup = new(routing,
            new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()),
            ValueHash256KademliaDistance.Instance, health,
            DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig()));
        Node? queried = null;

        await lookup.Lookup(known.Id.Hash.ValueHash256, 8, (node, _) =>
        {
            if (node.Id == seed.Id) return Task.FromResult<Node[]?>([forged]);
            queried = node;
            return Task.FromResult<Node[]?>(null);
        }, token);

        Assert.That(queried, Is.SameAs(known));
        health.Received(1).OnRequestFailed(Arg.Is<Node>(node => ReferenceEquals(node, known)));
        health.DidNotReceive().OnRequestFailed(Arg.Is<Node>(node => ReferenceEquals(node, forged)));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_bound_fresh_closer_candidates([Values] bool limitSeen, [Values(1, 3)] int alpha, [Values] bool silentMentions, CancellationToken token)
    {
        KademliaConfig<int> config = new()
        {
            Alpha = alpha,
            LookupQueryLimit = limitSeen ? 1000 : 8,
            LookupSeenLimit = limitSeen ? 8 : 1000
        };
        IRoutingTable<int, int> routing = Substitute.For<IRoutingTable<int, int>>();
        routing.GetKNearestNeighbour(Arg.Any<int>(), Arg.Any<bool>()).Returns([100]);
        LookupKNearestNeighbour<int, int, int> lookup = new(routing, IntNodeHashProvider.Instance,
            Int32KademliaDistance.Instance, Substitute.For<INodeHealthTracker<int>>(), config);
        int requests = 0;
        int[] result = await lookup.Lookup(0, 16, (node, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult<int[]?>(silentMentions
                ? node % 2 != 0 ? null : node > 2 ? [node - 1, node - 2] : []
                : node > 1 ? [node - 1] : []);
        }, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Is.EqualTo(8));
            Assert.That(result, Does.Contain(100));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_deadline_should_cancel_requests_without_penalty_and_keep_results([Values] bool transportError, CancellationToken token)
    {
        ManualTimeProvider clock = new();
        KademliaConfig<int> config = new()
        {
            TimeProvider = clock,
            Alpha = 1,
            LookupTimeout = TimeSpan.FromMilliseconds(100),
            LookupFindNeighbourHardTimeout = TimeSpan.FromSeconds(2)
        };
        IRoutingTable<int, int> routing = Substitute.For<IRoutingTable<int, int>>();
        routing.GetKNearestNeighbour(Arg.Any<int>(), Arg.Any<bool>()).Returns([100]);
        INodeHealthTracker<int> health = Substitute.For<INodeHealthTracker<int>>();
        LookupKNearestNeighbour<int, int, int> lookup = new(routing, IntNodeHashProvider.Instance,
            Int32KademliaDistance.Instance, health, config);
        int active = 0;
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int[]> lookupTask = lookup.Lookup(0, 16, async (node, requestToken) =>
        {
            if (node == 100) return [99];
            Interlocked.Increment(ref active);
            pending.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, requestToken);
                return [];
            }
            catch (OperationCanceledException) when (transportError)
            {
                throw new IOException("Transport closed during cancellation");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, token);

        await pending.Task.WaitAsync(token);
        clock.AdvanceAndFireDueTimers(config.LookupTimeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await lookupTask, Is.EqualTo(new[] { 100 }));
            Assert.That(active, Is.Zero);
        }
        health.DidNotReceive().OnRequestFailed(Arg.Any<int>());
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Bootstrap_should_continue_after_lookup_budget_is_exhausted(CancellationToken token)
    {
        KademliaConfig<int> config = new() { Alpha = 1, LookupQueryLimit = 8, KSize = 16 };
        IRoutingTable<int, int> routing = Substitute.For<IRoutingTable<int, int>>();
        routing.GetKNearestNeighbour(Arg.Any<int>(), Arg.Any<bool>()).Returns([100]);
        routing.IterateBuckets().Returns([new RoutingTableBucket<int, int>(0, 0, [100])]);
        INodeHealthTracker<int> health = Substitute.For<INodeHealthTracker<int>>();
        LookupKNearestNeighbour<int, int, int> lookup = new(routing, IntNodeHashProvider.Instance,
            Int32KademliaDistance.Instance, health, config);
        IKeyOperator<int, int, int> keys = Substitute.For<IKeyOperator<int, int, int>>();
        keys.GetKeyHash(Arg.Any<int>()).Returns(call => call.Arg<int>());
        keys.GetNodeHash(Arg.Any<int>()).Returns(call => call.Arg<int>());
        keys.CreateRandomKeyAtDistance(0, 0).Returns(1);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        int firstLookupRequests = 0;
        sender.FindNeighbours(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            int node = call.ArgAt<int>(0);
            if (call.ArgAt<int>(1) != 0) return Task.FromResult<int[]?>([]);
            firstLookupRequests++;
            return Task.FromResult<int[]?>(node > 1 ? [node - 1] : []);
        });
        Nethermind.Kademlia.Kademlia<int, int, int> kademlia = new(keys, sender, routing, lookup,
            health, config, NullLoggerFactory.Instance);

        await kademlia.Bootstrap(token);

        Assert.That(firstLookupRequests, Is.EqualTo(8));
        await sender.Received().FindNeighbours(100, 1, Arg.Any<CancellationToken>());
    }

    private const int Self = 0;
    private const int Seed1 = 1;
    private const int Seed2 = 2;
    private const int Seed3 = 3;
    private const int N1 = 4;
    private const int N2 = 5;

    private static (LookupKNearestNeighbour<int, int, int> Lookup, IRoutingTable<int, int> Routing, INodeHealthTracker<int> Health) CreateLookup(int alpha, TimeSpan hardTimeout, int[] seeds, ILoggerFactory? loggerFactory = null)
    {
        IRoutingTable<int, int> routing = Substitute.For<IRoutingTable<int, int>>();
        routing.GetKNearestNeighbour(Arg.Any<int>(), Arg.Any<bool>()).Returns(seeds);

        INodeHealthTracker<int> health = Substitute.For<INodeHealthTracker<int>>();

        LookupKNearestNeighbour<int, int, int> lookup = new(
            routing,
            IntNodeHashProvider.Instance,
            Int32KademliaDistance.Instance,
            health,
            new KademliaConfig<int>
            {
                CurrentNodeId = Self,
                Alpha = alpha,
                KSize = 8,
                LookupFindNeighbourHardTimeout = hardTimeout,
            },
            loggerFactory ?? NullLoggerFactory.Instance);

        return (lookup, routing, health);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_not_penalize_locally_refused_requests(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, INodeHealthTracker<int> health) =
            CreateLookup(1, TimeSpan.FromSeconds(1), [Seed1]);

        int[] result = await lookup.Lookup(Self, 8,
            (_, _) => Task.FromException<int[]?>(new KademliaRequestNotSentException()), token);

        Assert.That(result, Is.Empty);
        health.DidNotReceive().OnRequestFailed(Arg.Any<int>());
        health.DidNotReceive().OnIncomingMessageFrom(Arg.Any<int>());
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_handles_cancelled_requests_without_rethrowing([Values] bool cancelLookup, CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, INodeHealthTracker<int> health) =
            CreateLookup(1, TimeSpan.FromMilliseconds(100), [Seed1]);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource<int[]?> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        AsyncLocal<bool> observing = new() { Value = true };
        int exceptions = 0;
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (observing.Value && args.Exception is OperationCanceledException) Interlocked.Increment(ref exceptions);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnException;
        int[] result;
        try
        {
            result = await lookup.Lookup(Self, 8, (_, findToken) =>
            {
                registration = findToken.Register(() => request.TrySetCanceled(findToken));
                if (cancelLookup) caller.Cancel();
                return request.Task;
            }, caller.Token);
        }
        finally
        {
            observing.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
            registration.Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Empty);
            Assert.That(request.Task.IsCanceled, Is.True);
            Assert.That(exceptions, Is.Zero);
        }
        health.Received(cancelLookup ? 0 : 1).OnRequestFailed(Seed1);
        health.DidNotReceive().OnIncomingMessageFrom(Arg.Any<int>());
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_unblock_on_mid_flight_cancellation(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, INodeHealthTracker<int> health) =
            CreateLookup(alpha, TimeSpan.FromSeconds(30), [Seed1]);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        TaskCompletionSource requestInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int[]> task = lookup.Lookup(
            Seed1,
            8,
            async (_, t) =>
            {
                requestInFlight.TrySetResult();
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            cts.Token);

        await requestInFlight.Task.WaitAsync(token);
        await cts.CancelAsync();

        _ = await task;
        health.DidNotReceive().OnRequestFailed(Seed1);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_record_request_failure_on_hard_timeout(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, INodeHealthTracker<int> health) =
            CreateLookup(alpha, TimeSpan.FromMilliseconds(100), [Seed1]);

        _ = await lookup.Lookup(
            Seed1,
            8,
            async (_, t) =>
            {
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            token);

        health.Received(1).OnRequestFailed(Seed1);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_not_mark_node_healthy_when_find_neighbours_returns_null(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, INodeHealthTracker<int> health) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1]);

        _ = await lookup.Lookup(
            Seed1,
            8,
            (_, _) => Task.FromResult<int[]?>(null),
            token);

        health.DidNotReceive().OnIncomingMessageFrom(Seed1);
        health.Received(1).OnRequestFailed(Seed1);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_record_peer_failure_on_find_neighbour_timeout(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, INodeHealthTracker<int> health) =
            CreateLookup(1, TimeSpan.FromMilliseconds(50), [Seed1]);

        _ = await lookup.Lookup(
            Seed1,
            8,
            async (_, t) =>
            {
                await Task.Delay(Timeout.Infinite, t);
                return null;
            },
            token);

        health.Received().OnRequestFailed(Seed1);
    }

    [TestCase(1)]
    [TestCase(3)]
    [CancelAfter(10000)]
    public async Task Lookup_should_return_results_with_different_alpha(int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(alpha, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3]);

        Dictionary<int, int[]> neighbours = new()
        {
            [Seed1] = [N1],
            [Seed2] = [N2],
            [Seed3] = [],
        };

        int[] result = await lookup.Lookup(
            Self,
            8,
            (node, _) => Task.FromResult<int[]?>(neighbours.GetValueOrDefault(node, [])),
            token);

        Assert.That(result, Is.Not.Empty);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_deduplicates_overlapping_and_cyclic_responses([Values(1, 3)] int alpha, CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(alpha, TimeSpan.FromSeconds(10), [Seed1, Seed1, Seed2, Seed3]);
        ConcurrentDictionary<int, int> requests = new();
        int[] graph = [Seed1, Seed2, Seed3, N1, N2];

        int[] result = await lookup.Lookup(Self, 8, async (node, _) =>
        {
            requests.AddOrUpdate(node, 1, static (_, count) => count + 1);
            await Task.Yield();
            return graph;
        }, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EquivalentTo(graph));
            Assert.That(requests.Select(static entry => entry.Key), Is.EquivalentTo(graph));
            Assert.That(requests.Select(static entry => entry.Value), Is.All.EqualTo(1));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_nodes_should_stream_routing_table_nodes_before_network_lookup_finishes(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1]);
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using IAsyncEnumerator<int> enumerator = lookup.LookupNodes(
            Self,
            8,
            async (_, findToken) =>
            {
                requestStarted.SetResult();
                await Task.Delay(Timeout.Infinite, findToken);
                return [];
            },
            token).GetAsyncEnumerator(token);

#pragma warning disable NUnit2045 // The assertions sequence an asynchronous state transition or wait prerequisite with dependent state checks; preserve fail-fast ordering.
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
#pragma warning restore NUnit2045
        Assert.That(enumerator.Current, Is.EqualTo(Seed1));
        await requestStarted.Task.WaitAsync(token);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_nodes_should_stop_when_enough_candidates_are_streamed(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1, Seed2]);
        int requests = 0;

        List<int> result = await lookup.LookupNodes(
            Self,
            1,
            (_, _) =>
            {
                requests++;
                return Task.FromResult<int[]?>([]);
            },
            token).ToListAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(new[] { Seed1 }));
            Assert.That(requests, Is.Zero);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_drain_cancelled_workers_before_returning(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(2, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3, N1]);
        TaskCompletionSource cancelledWorkerDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource seed1Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = await lookup.Lookup(
            Self,
            1,
            async (node, findToken) =>
            {
                if (node != Seed1)
                {
                    // Without this, the worker that dequeued Seed1 can be preempted until the other
                    // worker has run enough rounds to make it stop before it sends the request.
                    await seed1Requested.Task.WaitAsync(findToken);
                    return [];
                }

                seed1Requested.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, findToken);
                    return [];
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(100, CancellationToken.None);
                    cancelledWorkerDrained.SetResult();
                    throw;
                }
            },
            token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancelledWorkerDrained.Task.IsCompleted, Is.True);
            Assert.That(token.IsCancellationRequested, Is.False);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_transport_failure_is_not_logged_as_warning(CancellationToken token)
    {
        // A torn-down channel raises DotNetty's ClosedChannelException (: IOException); like a
        // SocketException it is expected transport churn, logged at Trace, never at Warning.
        CapturingLogger logger = new(LogLevel.Trace);
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1], new CapturingLoggerFactory(logger));

        _ = await lookup.Lookup(Self, 8, (_, _) => throw new IOException("channel closed"), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.False);
            Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Trace && e.Message.Contains("Find neighbour op failed")), Is.True);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_unexpected_failure_is_logged_as_warning([Values] bool faultedTask, CancellationToken token)
    {
        // A non-transport failure is unexpected and should still be reported.
        CapturingLogger logger = new();
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1], new CapturingLoggerFactory(logger));

        _ = await lookup.Lookup(Self, 8, (_, _) => faultedTask
            ? Task.FromException<int[]?>(new InvalidOperationException("boom"))
            : throw new InvalidOperationException("boom"), token);

        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.True);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_failure_after_cancellation_is_not_logged_as_warning(CancellationToken token)
    {
        // After the lookup token is cancelled, a non-transport teardown failure (e.g. DotNetty's
        // RejectedExecutionException from the disposed event loop) is expected and must stay quiet:
        // otherwise a graceful restart floods WARN and trips the very detector this fix targets.
        CapturingLogger logger = new();
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1], new CapturingLoggerFactory(logger));

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        _ = await lookup.Lookup(Self, 8, (_, _) =>
        {
            cts.Cancel();
            throw new InvalidOperationException("torn down");
        }, cts.Token);

        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.False);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_unexpected_failure_during_drain_is_logged_as_warning(CancellationToken token)
    {
        // The other seeds return nothing, so the lookup finishes and drains, cancelling its internal
        // token while the caller's token stays live. Seed1 is still in flight and then fails with a
        // non-transport exception: a real fault that must warn, not be silenced as shutdown teardown.
        CapturingLogger logger = new();
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(2, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3, N1], new CapturingLoggerFactory(logger));

        // Gate the empty-returning ops on Seed1's op having started, so Seed1's worker has taken its round
        // before the other worker consumes the Alpha*2 rounds that trip the no-better-result break:
        // otherwise Seed1's worker could break without ever calling the op and the drain never happens.
        TaskCompletionSource seed1Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = await lookup.Lookup(Self, 1, async (node, findToken) =>
        {
            if (node != Seed1)
            {
                await seed1Started.Task;
                return [];
            }

            seed1Started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, findToken);
                return [];
            }
            catch (OperationCanceledException)
            {
                // The drain cancelled the internal token; the caller's token is still live.
                throw new InvalidOperationException("failed during drain");
            }
        }, token);

        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("Find neighbour op failed")), Is.True);
    }

    private readonly record struct LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class CapturingLogger(LogLevel minLevel = LogLevel.Information) : ILogger
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        public IEnumerable<LogEntry> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        // Default to a production node's Info floor (Trace/Debug disabled) so the warning-gate is
        // actually exercised; a test that needs the Trace branch opts in with a lower minLevel.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
