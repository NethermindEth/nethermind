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
using Nethermind.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class LookupKNearestNeighbourTests
{
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

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
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

        Assert.That(result, Is.EqualTo(new[] { Seed1 }));
        Assert.That(requests, Is.Zero);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Lookup_should_drain_cancelled_workers_before_returning(CancellationToken token)
    {
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(2, TimeSpan.FromSeconds(10), [Seed1, Seed2, Seed3, N1]);
        TaskCompletionSource cancelledWorkerDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = await lookup.Lookup(
            Self,
            1,
            async (node, findToken) =>
            {
                if (node != Seed1)
                {
                    return [];
                }

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

        Assert.That(cancelledWorkerDrained.Task.IsCompleted, Is.True);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_transport_failure_is_not_logged_as_warning(CancellationToken token)
    {
        // A torn-down channel raises DotNetty's ClosedChannelException (: IOException); like a
        // SocketException it is expected transport churn — logged at Trace, never at Warning.
        CapturingLogger logger = new(LogLevel.Trace);
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1], new CapturingLoggerFactory(logger));

        _ = await lookup.Lookup(Self, 8, (_, _) => throw new IOException("channel closed"), token);

        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.False);
        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Trace), Is.True);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_unexpected_failure_is_logged_as_warning(CancellationToken token)
    {
        // A non-transport failure is unexpected and should still be reported.
        CapturingLogger logger = new();
        (LookupKNearestNeighbour<int, int, int> lookup, _, _) =
            CreateLookup(1, TimeSpan.FromSeconds(10), [Seed1], new CapturingLoggerFactory(logger));

        _ = await lookup.Lookup(Self, 8, (_, _) => throw new InvalidOperationException("boom"), token);

        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.True);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Find_neighbour_failure_after_cancellation_is_not_logged_as_warning(CancellationToken token)
    {
        // After the lookup token is cancelled, a non-transport teardown failure (e.g. DotNetty's
        // RejectedExecutionException from the disposed event loop) is expected and must stay quiet —
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
