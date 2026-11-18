// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class NodeHealthTrackerTests
{
    private const int Self = 0;
    private const int Remote = 1;
    private const int Stale = 2;

    private static (NodeHealthTracker<int, int, int> Tracker, RoutingTableStub Routing, IKademliaMessageSender<int, int> Sender) CreateTracker(
        int? toRefresh = null,
        int failureThreshold = 5,
        TimeSpan? refreshPingTimeout = null,
        IKademliaMessageSender<int, int>? sender = null)
    {
        RoutingTableStub routing = new() { ToRefresh = toRefresh };
        sender ??= Substitute.For<IKademliaMessageSender<int, int>>();
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = Self,
            NodeRequestFailureThreshold = failureThreshold,
        };
        if (refreshPingTimeout is { } timeout) config.RefreshPingTimeout = timeout;

        NodeHealthTracker<int, int, int> tracker = new(
            config,
            routing,
            IntNodeHashProvider.Instance,
            sender);
        return (tracker, routing, sender);
    }

    [Test]
    public void OnIncomingMessageFrom_ShouldPreserveSelfEndpoint()
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node replay = new(TestItem.PublicKeyA, "192.168.1.9", 30304);
        IHealthRoutingTable<Node, ValueHash256> routing = Substitute.For<IHealthRoutingTable<Node, ValueHash256>>();
        using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(
            new KademliaConfig<Node> { CurrentNodeId = self }, routing,
            new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()),
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());

        tracker.OnIncomingMessageFrom(replay);

        routing.Received(1).AddOrRefreshDeferred(self.Id.Hash.ValueHash256,
            Arg.Is<Node>(node => ReferenceEquals(node, self)), out _, out _);
    }

    [Test]
    public void OnIncomingMessageFrom_ShouldRefreshSelfWithSelfNode_WhenFullBucketSelectsSelf()
    {
        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(toRefresh: Self);

        tracker.OnIncomingMessageFrom(Remote);

        Assert.That(routing.AddCalls, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.AddCalls[1].Hash, Is.EqualTo(Self));
            Assert.That(routing.AddCalls[1].Node, Is.EqualTo(Self));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenRequestWasNotSent(CancellationToken token)
    {
        TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            attempted.SetResult();
            return Task.FromException<bool>(new KademliaRequestNotSentException());
        });
        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(toRefresh: Stale, sender: sender);

        tracker.OnIncomingMessageFrom(Remote);
        await attempted.Task.WaitAsync(token);
        await tracker.DisposeAsync();

        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_AfterOneLostPing(CancellationToken token)
    {
        TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempted.SetResult();
                return Task.FromResult(false);
            });

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        await attempted.Task.WaitAsync(token);
        await tracker.DisposeAsync();
        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task TryRefresh_ShouldKeepNode_WhenPingSucceeds(CancellationToken token)
    {
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(true);

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);

        await AssertEventuallyAsync(() => routing.HasAddedNode(Stale), token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(10000)]
    public async Task Dispose_ShouldCancelActiveRefreshWithoutRemovingNode(bool asyncDispose, CancellationToken token)
    {
        TaskCompletionSource pingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pingCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            CancellationToken pingToken = call.Arg<CancellationToken>();
            pingStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, pingToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                pingCancelled.SetResult();
                throw;
            }
        });

        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(
            toRefresh: Stale,
            refreshPingTimeout: TimeSpan.FromSeconds(10),
            sender: sender);

        tracker.OnIncomingMessageFrom(Remote);
        await pingStarted.Task.WaitAsync(token);

        if (asyncDispose)
        {
            await tracker.DisposeAsync();
        }
        else
        {
            tracker.Dispose();
        }

        await pingCancelled.Task.WaitAsync(token);
        Assert.That(routing.RemoveCalls, Does.Not.Contain(Stale));
    }

    [Test]
    public void OnRequestFailed_ShouldRequireConsecutiveFailureThreshold([Values(1, 3, 5)] int threshold, [Values] bool interveningSuccess)
    {
        (NodeHealthTracker<int, int, int> tracker, RoutingTableStub routing, _) = CreateTracker(failureThreshold: threshold);
        if (interveningSuccess)
        {
            for (int i = 0; i < threshold - 1; i++) tracker.OnRequestFailed(Remote);
            tracker.OnIncomingMessageFrom(Remote);
        }
        for (int i = 0; i < threshold - 1; i++) tracker.OnRequestFailed(Remote);
        Assert.That(routing.RemoveCalls, Is.Empty);
        tracker.OnRequestFailed(Remote);

        Assert.That(routing.RemoveCalls, Has.Count.EqualTo(1));
        Assert.That(routing.RemoveCalls[0], Is.EqualTo(Remote));
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, CancellationToken token)
    {
        for (int i = 0; i < 50; i++)
        {
            if (condition()) return;
            await Task.Delay(50, token);
        }
        Assert.Fail("Condition not met within timeout.");
    }

    [Test]
    public void Failures_at_another_endpoint_should_not_remove_verified_contact()
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node known = new(TestItem.PublicKeyB, "192.168.1.2", 30303) { ValidatedProtocol = false };
        Node forged = new(TestItem.PublicKeyB, "192.168.1.3", 30303);
        IHealthRoutingTable<Node, ValueHash256> routing = Substitute.For<IHealthRoutingTable<Node, ValueHash256>>();
        routing.TryGetActive(known.Id.Hash.ValueHash256, out _, out _).Returns(call =>
        {
            call[1] = known;
            call[2] = new object();
            return true;
        });
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.NodeRequestFailureThreshold = 2;
        using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(config, routing,
            new FromKeyNodeHashProvider<PublicKey, Node, ValueHash256>(new PublicKeyKeyOperator()),
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());

        for (int i = 0; i < 10; i++) tracker.OnRequestFailed(forged);
        tracker.OnRequestFailed(known);
        routing.DidNotReceive().Remove(Arg.Any<ValueHash256>(), Arg.Any<Node>(), Arg.Any<object>(), out _);
        tracker.OnRequestFailed(known);
        routing.Received(1).Remove(known.Id.Hash.ValueHash256, known, Arg.Any<object>(), out _);
    }

    [Test]
    public async Task Replacement_probe_chain_should_cap_work_even_for_oversized_custom_snapshot()
    {
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = 0,
            NodeRequestFailureThreshold = 1,
            RequireReplacementValidation = true,
            RefreshPingDelay = TimeSpan.Zero
        };
        IHealthRoutingTable<int, int> routing = Substitute.For<IHealthRoutingTable<int, int>>();
        routing.TryGetActive(1, out _, out _).Returns(call => { call[1] = 1; call[2] = new object(); return true; });
        routing.Remove(1, 1, Arg.Any<object>(), out _).Returns(true);
        int[] candidates = new int[100];
        for (int i = 0; i < candidates.Length; i++) candidates[i] = i + 2;
        routing.GetReplacementCandidates(1).Returns(candidates);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        await using NodeHealthTracker<int, int, int> tracker = new(config, routing, IntNodeHashProvider.Instance, sender);

        tracker.OnRequestFailed(1);

        await sender.Received(16).Ping(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await sender.DidNotReceive().Ping(18, Arg.Any<CancellationToken>());
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Replacement_promotion_should_probe_past_dead_candidate_and_publish_once([Values] bool localRefusal, CancellationToken token)
    {
        int first = (1 << 30) | 1;
        int second = (1 << 30) | 2;
        int live = (1 << 30) | 3;
        int dead = (1 << 30) | 4;
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = 0,
            KSize = 2,
            Beta = 0,
            NodeRequestFailureThreshold = 1,
            RequireReplacementValidation = true,
            RefreshPingDelay = TimeSpan.FromMilliseconds(1)
        };
        KBucketTree<int, int> routing = new(config, IntNodeHashProvider.Instance, Int32KademliaDistance.Instance);
        foreach (int node in new[] { first, second, live, dead }) routing.TryAddOrRefresh(node, node, out _);
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Ping(dead, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            firstProbe.TrySetResult();
            return localRefusal ? Task.FromException<bool>(new KademliaRequestNotSentException()) : Task.FromResult(false);
        });
        sender.Ping(live, Arg.Any<CancellationToken>()).Returns(_ => { started.TrySetResult(); return reply.Task; });
        await using NodeHealthTracker<int, int, int> tracker = new(config, routing, IntNodeHashProvider.Instance, sender);
        TaskCompletionSource promoted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int additions = 0;
        routing.OnNodeAdded += (_, node) => { if (node == live) { Interlocked.Increment(ref additions); promoted.TrySetResult(); } };

        tracker.OnRequestFailed(first);
        if (localRefusal)
        {
            await firstProbe.Task.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(routing.TryGet(dead, out _), Is.True);
                Assert.That(routing.TryGet(live, out _), Is.True);
            }
            tracker.OnRequestFailed(second);
        }
        await started.Task.WaitAsync(token);
        Assert.That(routing.GetAllAtDistance(31), Is.EqualTo(localRefusal ? [] : new[] { second }));
        reply.SetResult(true);
        await promoted.Task.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.GetAllAtDistance(31), Is.EquivalentTo(localRefusal ? new[] { live } : new[] { second, live }));
            Assert.That(additions, Is.EqualTo(1));
        }
        await sender.Received(1).Ping(dead, Arg.Any<CancellationToken>());
        await sender.Received(1).Ping(live, Arg.Any<CancellationToken>());
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Concurrent_success_should_reset_failures_before_later_failure([Values] bool customTable, CancellationToken token)
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node peer = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.NodeRequestFailureThreshold = 2;
        FromKeyNodeHashProvider<PublicKey, Node, ValueHash256> hashProvider = new(new PublicKeyKeyOperator());
        KBucketTree<Node, ValueHash256> routing = new(config, hashProvider, ValueHash256KademliaDistance.Instance);
        IHealthRoutingTable<Node, ValueHash256> registered = customTable ? CreateCustomTable(routing) : routing;
        using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(config, registered, hashProvider,
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());
        tracker.OnIncomingMessageFrom(peer);
        tracker.OnRequestFailed(peer);
        routing.Remove(peer.Id.Hash.ValueHash256);
        TaskCompletionSource added = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failureStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseSuccess = new();
        routing.OnNodeAdded += (_, _) =>
        {
            added.TrySetResult();
            releaseSuccess.Wait(token);
        };
        Task success = Task.Run(() => tracker.OnIncomingMessageFrom(peer), token);
        Task failure = Task.CompletedTask;
        try
        {
            await added.Task.WaitAsync(token);
            failure = Task.Run(() =>
            {
                failureStarted.SetResult();
                tracker.OnRequestFailed(peer);
            }, token);
            await failureStarted.Task.WaitAsync(token);
            await Task.WhenAny(failure, Task.Delay(100, token));
        }
        finally
        {
            releaseSuccess.Set();
            await Task.WhenAll(success, failure);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.TryGet(peer.Id.Hash.ValueHash256, out Node? retained), Is.True);
            Assert.That(retained, Is.SameAs(peer));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Endpoint_replacement_should_survive_old_request_failure([Values] bool replaceDuringAttribution, CancellationToken token)
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node original = new(TestItem.PublicKeyB, "192.168.1.2", 30303) { ValidatedProtocol = false };
        Node replacement = new(TestItem.PublicKeyB, "192.168.1.3", 30303) { ValidatedProtocol = false };
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.NodeRequestFailureThreshold = 1;
        FromKeyNodeHashProvider<PublicKey, Node, ValueHash256> hashProvider = new(new PublicKeyKeyOperator());
        KBucketTree<Node, ValueHash256> routing = new(config, hashProvider, ValueHash256KademliaDistance.Instance);
        routing.TryAddOrRefresh(original.Id.Hash.ValueHash256, original, out _);
        if (replaceDuringAttribution)
        {
            config.AreSameEndpoint = (known, failed) =>
            {
                routing.TryAddOrRefresh(replacement.Id.Hash.ValueHash256, replacement, out _);
                return known.DiscoveryAddress.Equals(failed.DiscoveryAddress);
            };
        }
        await using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(config, routing, hashProvider,
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());
        LookupKNearestNeighbour<PublicKey, Node, ValueHash256> lookup = new(routing, hashProvider,
            ValueHash256KademliaDistance.Instance, tracker, config);
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Node[]?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int removed = 0;
        routing.OnNodeRemoved += (_, _) => removed++;
        Task<Node[]> request = lookup.Lookup(original.Id.Hash.ValueHash256, 8, (node, _) =>
        {
            Assert.That(node, Is.SameAs(original));
            requested.TrySetResult();
            return response.Task;
        }, token);

        await requested.Task.WaitAsync(token);
        if (!replaceDuringAttribution) routing.TryAddOrRefresh(replacement.Id.Hash.ValueHash256, replacement, out _);
        response.SetResult(null);
        await request;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.TryGet(replacement.Id.Hash.ValueHash256, out Node? retained), Is.True);
            Assert.That(retained, Is.SameAs(replacement));
            Assert.That(removed, Is.Zero);
        }
    }

    [Test]
    public async Task Insertions_should_not_repeatedly_probe_an_incumbent([Values] bool responds, [Values] bool customTable)
    {
        RoutingTableStub routing = new() { ToRefresh = Stale, RepeatedRefresh = true };
        IHealthRoutingTable<int, int> registered = routing;
        if (customTable)
        {
            registered = Substitute.For<IHealthRoutingTable<int, int>>();
            registered.AddOrRefreshDeferred(Arg.Any<int>(), Arg.Any<int>(), out _, out _).Returns(call =>
            {
                BucketAddResult result = routing.TryAddOrRefresh(call.ArgAt<int>(0), call.ArgAt<int>(1), out int refresh);
                call[2] = refresh;
                call[3] = default(RoutingTableNotifications<int>);
                return result;
            });
            registered.TryGetActive(Arg.Any<int>(), out _, out _).Returns(call =>
            {
                bool found = routing.TryGetActive(call.ArgAt<int>(0), out int node, out object? revision);
                call[1] = node;
                call[2] = revision;
                return found;
            });
            registered.Remove(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<object>(), out _).Returns(call =>
            {
                bool removed = routing.Remove(call.ArgAt<int>(0), call.ArgAt<int>(1), call.ArgAt<object>(2), out RoutingTableNotifications<int> notifications);
                call[3] = notifications;
                return removed;
            });
        }
        ManualTimeProvider time = new();
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Stale, Arg.Any<CancellationToken>()).Returns(responds);
        await using NodeHealthTracker<int, int, int> tracker = new(
            new KademliaConfig<int> { CurrentNodeId = Self, NodeRequestFailureThreshold = 2, RefreshPingDelay = TimeSpan.Zero },
            registered, IntNodeHashProvider.Instance, sender)
        { RefreshTimeProvider = time };

        tracker.OnIncomingMessageFrom(Remote);
        for (int i = 0; i < 10; i++) tracker.OnIncomingMessageFrom(Remote);

        await sender.Received(1).Ping(Stale, Arg.Any<CancellationToken>());
        Assert.That(routing.RemoveCalls, Is.Empty);

        time.Advance(TimeSpan.FromSeconds(30));
        tracker.OnIncomingMessageFrom(Remote);

        await sender.Received(2).Ping(Stale, Arg.Any<CancellationToken>());
        Assert.That(routing.RemoveCalls, Is.EqualTo(responds ? Array.Empty<int>() : [Stale]));
    }

    private sealed class RoutingTableStub : IHealthRoutingTable<int, int>
    {
        public int? ToRefresh { get; init; }

        public bool RepeatedRefresh { get; init; }

        public List<(int Hash, int Node)> AddCalls { get; } = [];

        public List<int> RemoveCalls { get; } = [];

        public BucketAddResult TryAddOrRefresh(in int hash, int item, out int toRefresh)
        {
            bool isFirstAdd;
            lock (AddCalls)
            {
                AddCalls.Add((hash, item));
                isFirstAdd = AddCalls.Count == 1;
            }

            if ((isFirstAdd || RepeatedRefresh && item == Remote) && ToRefresh is not null)
            {
                toRefresh = ToRefresh.Value;
                return BucketAddResult.Full;
            }

            toRefresh = default;
            return BucketAddResult.Refreshed;
        }

        public bool HasAddedNode(int hash)
        {
            lock (AddCalls)
            {
                foreach ((int h, int _) in AddCalls)
                {
                    if (h == hash) return true;
                }
            }
            return false;
        }

        public bool Remove(in int hash)
        {
            lock (RemoveCalls) RemoveCalls.Add(hash);
            return true;
        }

        private readonly object _revision = new();

        public bool TryGetActive(in int hash, out int node, [NotNullWhen(true)] out object? revision)
        {
            revision = _revision;
            return TryGet(hash, out node);
        }

        public BucketAddResult AddOrRefreshDeferred(in int hash, int node, out int toRefresh, out RoutingTableNotifications<int> notifications)
        {
            notifications = default;
            return TryAddOrRefresh(hash, node, out toRefresh);
        }

        public bool Remove(in int hash, int expected, object revision, out RoutingTableNotifications<int> notifications)
        {
            notifications = default;
            return ReferenceEquals(revision, _revision) && hash == expected && Remove(hash);
        }

        public void Publish(in RoutingTableNotifications<int> notifications) { }

        public int[] GetKNearestNeighbour(int hash, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public int[] GetKNearestNeighbourExcluding(int hash, int exclude, bool excludeSelf = false) =>
            throw new NotSupportedException();

        public int[] GetAllAtDistance(int i) => throw new NotSupportedException();

        public IEnumerable<RoutingTableBucket<int, int>> IterateBuckets() =>
            throw new NotSupportedException();

        public bool TryGet(in int hash, out int node)
        {
            node = hash;
            return true;
        }

        public void LogDebugInfo() => throw new NotSupportedException();

        public event EventHandler<int>? OnNodeAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<int>? OnNodeRemoved
        {
            add { }
            remove { }
        }

        public RoutingTableOccupancy GetOccupancy() => throw new NotSupportedException();
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Custom_table_serializes_a_same_value_health_refresh_with_failure_removal(CancellationToken token)
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node peer = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.NodeRequestFailureThreshold = 2;
        FromKeyNodeHashProvider<PublicKey, Node, ValueHash256> hashProvider = new(new PublicKeyKeyOperator());
        KBucketTree<Node, ValueHash256> tree = new(config, hashProvider, ValueHash256KademliaDistance.Instance);
        IHealthRoutingTable<Node, ValueHash256> builtIn = tree;
        IHealthRoutingTable<Node, ValueHash256> custom = CreateCustomTable(tree);
        TaskCompletionSource removing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseRemoval = new();
        custom.Remove(Arg.Any<ValueHash256>(), Arg.Any<Node>(), Arg.Any<object>(), out _).Returns(call =>
        {
            removing.TrySetResult();
            releaseRemoval.Wait(token);
            bool removed = builtIn.Remove(call.ArgAt<ValueHash256>(0), call.ArgAt<Node>(1), call.ArgAt<object>(2),
                out RoutingTableNotifications<Node> notifications);
            call[3] = notifications;
            return removed;
        });
        await using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(config, custom, hashProvider,
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());
        tracker.OnIncomingMessageFrom(peer);
        tracker.OnRequestFailed(peer);
        Task failure = Task.Run(() => tracker.OnRequestFailed(peer), token);
        Task refresh = Task.CompletedTask;
        TaskCompletionSource refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task observed = await Task.WhenAny(removing.Task, failure).WaitAsync(token);
            if (observed == failure) await failure;
            await removing.Task.WaitAsync(token);
            refresh = Task.Run(() =>
            {
                refreshStarted.TrySetResult();
                tracker.OnIncomingMessageFrom(peer);
            }, token);
            await refreshStarted.Task.WaitAsync(token);
        }
        finally
        {
            releaseRemoval.Set();
            await Task.WhenAll(failure, refresh);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(peer.Id.Hash.ValueHash256, out Node? retained), Is.True);
            Assert.That(retained, Is.SameAs(peer));
        }
    }

    [Test]
    public void Direct_same_value_refresh_prevents_a_stale_health_removal([Values] bool directRefresh)
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node peer = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.NodeRequestFailureThreshold = 1;
        FromKeyNodeHashProvider<PublicKey, Node, ValueHash256> hashes = new(new PublicKeyKeyOperator());
        KBucketTree<Node, ValueHash256> tree = new(config, hashes, ValueHash256KademliaDistance.Instance);
        IHealthRoutingTable<Node, ValueHash256> builtIn = tree;
        IHealthRoutingTable<Node, ValueHash256> custom = CreateCustomTable(tree);
        custom.Remove(Arg.Any<ValueHash256>(), Arg.Any<Node>(), Arg.Any<object>(), out _).Returns(call =>
        {
            if (directRefresh) tree.TryAddOrRefresh(peer.Id.Hash.ValueHash256, peer, out _);
            bool removed = builtIn.Remove(call.ArgAt<ValueHash256>(0), call.ArgAt<Node>(1), call.ArgAt<object>(2),
                out RoutingTableNotifications<Node> notifications);
            call[3] = notifications;
            return removed;
        });
        using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(config, custom, hashes,
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());
        tracker.OnIncomingMessageFrom(peer);
        int removals = 0;
        tree.OnNodeRemoved += (_, _) => removals++;
        tracker.OnRequestFailed(peer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.TryGet(peer.Id.Hash.ValueHash256, out _), Is.EqualTo(directRefresh));
            Assert.That(removals, Is.EqualTo(directRefresh ? 0 : 1));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Subscriber_lock_does_not_block_another_incoming_contact(
        [Values] bool removing, [Values] bool customTable, CancellationToken token)
    {
        Node self = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        Node peer = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
        Node other = new(TestItem.PublicKeyC, "192.168.1.3", 30303);
        KademliaConfig<Node> config = DiscoveryKademliaConfigFactory.Create(self, [], new DiscoveryConfig());
        config.NodeRequestFailureThreshold = 1;
        FromKeyNodeHashProvider<PublicKey, Node, ValueHash256> hashProvider = new(new PublicKeyKeyOperator());
        KBucketTree<Node, ValueHash256> tree = new(config, hashProvider, ValueHash256KademliaDistance.Instance);
        IHealthRoutingTable<Node, ValueHash256> routing = tree;
        if (customTable) routing = CreateCustomTable(tree);
        await using NodeHealthTracker<PublicKey, Node, ValueHash256> tracker = new(config, routing, hashProvider,
            Substitute.For<IKademliaMessageSender<PublicKey, Node>>());
        if (removing) tree.TryAddOrRefresh(peer.Id.Hash.ValueHash256, peer, out _);
        object subscriberGate = new();
        using ManualResetEventSlim gateHeld = new();
        using ManualResetEventSlim callbackEntered = new();
        bool acquired = false;
        void Subscriber(object? sender, Node node)
        {
            if (!node.Id.Equals(peer.Id)) return;
            callbackEntered.Set();
            acquired = Monitor.TryEnter(subscriberGate, TimeSpan.FromSeconds(2));
            if (acquired) Monitor.Exit(subscriberGate);
        }
        if (removing) tree.OnNodeRemoved += Subscriber;
        else tree.OnNodeAdded += Subscriber;

        Task incoming = Task.Run(() =>
        {
            lock (subscriberGate)
            {
                gateHeld.Set();
                callbackEntered.Wait(token);
                tracker.OnIncomingMessageFrom(other);
            }
        }, token);
        gateHeld.Wait(token);
        Task mutation = Task.Run(() =>
        {
            if (removing) tracker.OnRequestFailed(peer);
            else tracker.OnIncomingMessageFrom(peer);
        }, token);
        await Task.WhenAll(incoming, mutation).WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(acquired, Is.True, "A subscriber holding its own lock must be able to re-enter the tracker from another thread.");
            Assert.That(tree.TryGet(other.Id.Hash.ValueHash256, out _), Is.True);
            Assert.That(tree.TryGet(peer.Id.Hash.ValueHash256, out _), Is.EqualTo(!removing));
        }
    }

    private static IHealthRoutingTable<Node, ValueHash256> CreateCustomTable(KBucketTree<Node, ValueHash256> tree)
    {
        IHealthRoutingTable<Node, ValueHash256> builtIn = tree;
        IHealthRoutingTable<Node, ValueHash256> custom = Substitute.For<IHealthRoutingTable<Node, ValueHash256>>();
        custom.AddOrRefreshDeferred(Arg.Any<ValueHash256>(), Arg.Any<Node>(), out _, out _).Returns(call =>
        {
            BucketAddResult result = builtIn.AddOrRefreshDeferred(call.ArgAt<ValueHash256>(0), call.ArgAt<Node>(1),
                out Node? refresh, out RoutingTableNotifications<Node> notifications);
            call[2] = refresh;
            call[3] = notifications;
            return result;
        });
        custom.TryGet(Arg.Any<ValueHash256>(), out _).Returns(call =>
        {
            bool found = tree.TryGet(call.ArgAt<ValueHash256>(0), out Node? node);
            call[1] = node;
            return found;
        });
        custom.TryGetActive(Arg.Any<ValueHash256>(), out _, out _).Returns(call =>
        {
            bool found = builtIn.TryGetActive(call.ArgAt<ValueHash256>(0), out Node? node, out object? revision);
            call[1] = node;
            call[2] = revision;
            return found;
        });
        custom.Remove(Arg.Any<ValueHash256>(), Arg.Any<Node>(), Arg.Any<object>(), out _).Returns(call =>
        {
            bool removed = builtIn.Remove(call.ArgAt<ValueHash256>(0), call.ArgAt<Node>(1), call.ArgAt<object>(2),
                out RoutingTableNotifications<Node> notifications);
            call[3] = notifications;
            return removed;
        });
        custom.When(table => table.Publish(Arg.Any<RoutingTableNotifications<Node>>()))
            .Do(call => builtIn.Publish(call.Arg<RoutingTableNotifications<Node>>()));
        custom.GetReplacementCandidates(Arg.Any<ValueHash256>()).Returns(call => tree.GetReplacementCandidates(call.Arg<ValueHash256>()));
        custom.IterateBuckets().Returns(_ => tree.IterateBuckets());
        return custom;
    }

    [Test]
    public async Task Silent_tail_requires_five_probes_at_least_thirty_seconds_apart()
    {
        int first = (1 << 30) | 1;
        int second = (1 << 30) | 2;
        int candidate = (1 << 30) | 3;
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = 0,
            KSize = 2,
            Beta = 0,
            RequireReplacementValidation = true,
            RefreshPingDelay = TimeSpan.Zero
        };
        KBucketTree<int, int> routing = new(config, IntNodeHashProvider.Instance, Int32KademliaDistance.Instance);
        routing.TryAddOrRefresh(first, first, out _);
        routing.TryAddOrRefresh(second, second, out _);
        List<int> probes = [];
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            int node = call.Arg<int>();
            probes.Add(node);
            return node == candidate;
        });
        ManualTimeProvider time = new();
        await using NodeHealthTracker<int, int, int> tracker = new(config, routing, IntNodeHashProvider.Instance, sender)
        {
            RefreshTimeProvider = time
        };

        for (int attempt = 0; attempt < 5; attempt++)
        {
            tracker.OnIncomingMessageFrom(candidate);
            Assert.That(routing.TryGet(first, out _), Is.EqualTo(attempt < 4));
            if (attempt == 4) break;
            time.Advance(TimeSpan.FromMilliseconds(29999));
            tracker.OnIncomingMessageFrom(candidate);
            Assert.That(probes, Has.Count.EqualTo(attempt + 1));
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(probes, Is.EqualTo(new[] { first, first, first, first, first, candidate }));
            Assert.That(routing.GetAllAtDistance(31), Is.EquivalentTo(new[] { second, candidate }));
        }
    }

    [Test]
    public async Task Only_a_live_contact_failure_opens_a_replacement_probe_chain([Values] bool liveFailure)
    {
        int first = (1 << 30) | 1;
        int second = (1 << 30) | 2;
        int third = (1 << 30) | 3;
        int fourth = (1 << 30) | 4;
        KademliaConfig<int> config = new()
        {
            CurrentNodeId = 0,
            KSize = 2,
            Beta = 0,
            NodeRequestFailureThreshold = 1,
            RequireReplacementValidation = true,
            RefreshPingDelay = TimeSpan.Zero
        };
        KBucketTree<int, int> routing = new(config, IntNodeHashProvider.Instance, Int32KademliaDistance.Instance);
        foreach (int node in new[] { first, second, third, fourth }) routing.TryAddOrRefresh(node, node, out _);
        List<int> probes = [];
        IKademliaMessageSender<int, int> sender = Substitute.For<IKademliaMessageSender<int, int>>();
        sender.Ping(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            probes.Add(call.Arg<int>());
            return false;
        });
        await using NodeHealthTracker<int, int, int> tracker = new(config, routing, IntNodeHashProvider.Instance, sender);

        tracker.OnRequestFailed(liveFailure ? first : third);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routing.GetAllAtDistance(31), Is.EquivalentTo(liveFailure ? new[] { second } : new[] { first, second }));
            Assert.That(routing.TryGet(third, out _), Is.True);
            Assert.That(probes, Is.EqualTo(liveFailure ? new[] { fourth, third } : Array.Empty<int>()));
        }
    }
}
