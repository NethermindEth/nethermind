// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Test.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using IEventExecutorGroup = DotNetty.Common.Concurrency.IEventExecutorGroup;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class PeerManagerTests
    {
        [Test]
        public async Task Incoming_ban_queries_recover_at_the_original_injected_deadline([Values] IncomingBanStorage storage)
        {
            ManualTimeProvider clock = new();
            await using Context context = new(maxStats: 1, timeProvider: clock);
            bool historyOnly = storage == IncomingBanStorage.HistoryOnly;
            if (historyOnly) context.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303, true));
            Node remote = new(TestItem.PublicKeyB, "8.8.8.8", 30303);
            context.Stats.ReportDisconnect(remote, DisconnectType.Local, DisconnectReason.BreachOfProtocol);
            clock.Advance(TimeSpan.FromMinutes(2));
            if (storage == IncomingBanStorage.Recreated)
            {
                INodeStats original = context.Stats.GetOrAdd(remote);
                context.Stats.GetOrAdd(new Node(TestItem.PublicKeyC, "9.9.9.9", 30303));
                Assert.That(context.Stats.GetOrAdd(remote), Is.Not.SameAs(original));
            }
            context.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(remote.Id));
            Assert.That(context.Sessions.Single().IsClosing, Is.True);
            clock.Advance(TimeSpan.FromMinutes(13) - TimeSpan.FromTicks(1));
            context.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(remote.Id));
            Assert.That(context.Sessions.Last().IsClosing, Is.True);
            clock.Advance(TimeSpan.FromTicks(1));
            context.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(remote.Id));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Sessions.Last().IsClosing, Is.False);
                Assert.That(context.PeerManager.ActivePeers.Count, Is.EqualTo(1));
                Assert.That(context.PeerPool.Peers.ContainsKey(remote.Id), Is.EqualTo(!historyOnly));
            }
        }

        public enum IncomingBanStorage { Retained, HistoryOnly, Recreated }

        [Test]
        public async Task Inbound_attachment_and_removal_use_the_selected_peer_instance()
        {
            await using Context context = new();
            IPeerPool forwarding = CreateForwardingPool(context);
            ReplacePeerManager(context, forwarding);
            context.PeerManager.Start();
            Node remote = new(TestItem.PublicKeyB, "8.8.8.8", 30303);
            Peer retired = context.PeerPool.GetOrAdd(remote);
            Assert.That(context.PeerPool.TryRemove(remote.Id, out _), Is.True);
            forwarding.TryGet(remote.Id, out Arg.Any<Peer>()).Returns(call =>
            {
                call[1] = retired;
                return true;
            });
            context.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(remote.Id));
            Assert.That(context.Sessions.Last().IsClosing, Is.True);
            Peer replacement = context.PeerPool.GetOrAdd(remote);
            ISession open = Substitute.For<ISession>();
            replacement.InSession = open;
            context.PeerPool.ActivePeers[remote.Id] = replacement;
            forwarding.PeerRemoved += Raise.EventWith(new PeerEventArgs(retired));
            forwarding.PeerRemoved += Raise.EventWith(new PeerEventArgs(replacement));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.PeerPool.ActivePeers[remote.Id], Is.SameAs(replacement));
                Assert.That(replacement.InSession, Is.SameAs(open));
                Assert.That(replacement.InSession!.IsClosing, Is.False);
            }
        }

        [Test]
        public async Task Forwarding_pool_preserves_incoming_candidate_attribution_and_owned_stats()
        {
            await using Context context = new(maxStats: 4);
            IPeerPool forwarding = CreateForwardingPool(context);
            ReplacePeerManager(context, forwarding);
            context.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Assert.That(context.PeerPool.TryGet(TestItem.PublicKeyB, out Peer peer), Is.True);
            INodeStats retained = context.Stats.GetOrAdd(peer.Node);
            for (int i = 10; i < 20; i++)
                context.Stats.ReportEvent(new Node(TestItem.PublicKeys[i], "9.9.9.9", 30303), NodeStatsEventType.DiscoveryPongIn);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.IsInboundCandidate, Is.True);
                Assert.That(peer.Stats, Is.SameAs(retained));
                Assert.That(context.Stats.GetOrAdd(peer.Node), Is.SameAs(retained));
                Assert.That(context.Sessions.Single().IsClosing, Is.False);
                Assert.That(context.PeerManager.ActivePeers.Single(), Is.SameAs(peer));
            }
            forwarding.Received(1).TryGetOrAdd(Arg.Is<Node>(node => node.Id == TestItem.PublicKeyB), out Arg.Any<Peer?>(), true);
        }

        [Test]
        public async Task Can_start_and_stop()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            await ctx.PeerManager.StopAsync();
        }

        [Test]
        public async Task Candidate_selection_recovers_after_repeated_failures()
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(timeProvider: clock);
            ctx.NetworkConfig.PeersUpdateInterval = 10;
            INodeStats stats = Substitute.For<INodeStats>();
            int attempts = 0;
            stats.IsConnectionDelayed(Arg.Any<DateTime>()).Returns(_ =>
            {
                if (Interlocked.Increment(ref attempts) <= 11) throw new InvalidOperationException("injected selection failure");
                return (false, null);
            });
            Node node = new(ctx.CreateNodes(1)[0]);
            ctx.PeerPool.Peers.TryAdd(node.Id, new Peer(node, stats));
            ctx.PeerManager.Start();

            await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 11; i++)
            {
                Task nextTimer = clock.NextTimerCreated;
                clock.AdvanceAndFireTimer(clock.DueTimes[^1]);
                if (i < 10) await nextTimer.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(15));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.GreaterThan(0));
                Assert.That(attempts, Is.GreaterThan(11));
            }
        }

        [Test]
        public async Task Filtered_candidates_back_off_without_delaying_other_peers([Values(1, 4)] int parallelism, [Values] bool isStatic)
        {
            const int candidates = 8;
            ManualTimeProvider timeProvider = new();
            await using Context ctx = new(parallelism, maxActivePeers: candidates + 1, timeProvider: timeProvider);
            ctx.NetworkConfig.PeersUpdateInterval = 10;
            ctx.RlpxPeer.UseRecentIpFilter();
            Assert.That(ctx.RlpxPeer.ShouldContact(IPAddress.Parse("52.141.78.53")), Is.True);
            foreach (NetworkNode node in ctx.CreateNodes(candidates)) ctx.PeerPool.GetOrAdd(new Node(node, isStatic));
            ctx.PeerManager.Start();

            int initialChecks = ctx.RlpxPeer.ShouldContactCallsCount;
            await timeProvider.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.ShouldContactCallsCount, Is.LessThanOrEqualTo(initialChecks + candidates * 2));
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero);
            }

            NetworkNode eligible = new(ctx.GenerateEnode().Replace("52.141.78.53", "52.141.78.54"));
            ctx.PeerPool.GetOrAdd(eligible);
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(5));
            Assert.That(ctx.PeerPool.ActivePeers.ContainsKey(eligible.NodeId), Is.True);

            ctx.RlpxPeer.UseRecentIpFilter();
            timeProvider.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
            await ctx.RlpxPeer.WaitForConnectCallsAsync(2, TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task Queued_candidate_cannot_activate_after_reclamation()
        {
            await using Context context = new();
            Node node = new(TestItem.PublicKeyB, "8.8.8.8", 30303);
            Peer queued = context.PeerPool.GetOrAdd(node);
            PeerPool pool = (PeerPool)context.PeerPool;
            Assert.That(pool.TryRemoveInactive(queued, ordinaryCandidateOnly: true), Is.True);
            Peer replacement = pool.GetOrAdd(node);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.PeerManager.TryActivateCandidate(queued), Is.False);
                Assert.That(context.PeerManager.TryActivateCandidate(replacement), Is.True);
                Assert.That(pool.TryRemoveInactive(replacement, ordinaryCandidateOnly: true), Is.False);
                Assert.That(pool.ActivePeers[node.Id], Is.SameAs(replacement));
            }
        }

        [Test]
        public async Task Filtered_static_candidates_do_not_repeat_selection([Values] bool staticOnly)
        {
            int selections = 0;
            TaskCompletionSource selectionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int startupReads = 0;
            int startupThread = Environment.CurrentManagedThreadId;
            bool starting = false;
            Node node = new(TestItem.PublicKeyB, "52.141.78.53", 30303, isStatic: true);
            Peer peer = new(node, Substitute.For<INodeStats>());
            ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
            if (!staticOnly) peers.TryAdd(node.Id, peer);

            IPeerPool peerPool = Substitute.For<IPeerPool>();
            peerPool.Peers.Returns(_ =>
            {
                if (starting && Environment.CurrentManagedThreadId == startupThread)
                    Interlocked.Increment(ref startupReads);
                else
                {
                    Interlocked.Increment(ref selections);
                    selectionStarted.TrySetResult();
                }
                return peers;
            });
            peerPool.ActivePeers.Returns(new ConcurrentDictionary<PublicKeyAsKey, Peer>());
            peerPool.StaticPeers.Returns(new[] { peer });
            IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
            ManualTimeProvider timeProvider = new();
            NetworkConfig config = new() { MaxActivePeers = 2, NumConcurrentOutgoingConnects = 1, PeersUpdateInterval = 10 };
            PeerManager manager = new(rlpxHost, peerPool, Substitute.For<INodeStatsManager>(), config,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance, timeProvider);

            starting = true;
            manager.Start();
            starting = false;
            try
            {
                Assert.That(startupReads, Is.EqualTo(1), "startup seeds the existing inbound-candidate counts once");
                await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(Volatile.Read(ref selections), Is.GreaterThan(0));
                await timeProvider.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
                await manager.StopAsync();
                Assert.That(Volatile.Read(ref selections), Is.LessThanOrEqualTo(4));
            }
            finally
            {
                await manager.StopAsync();
            }
        }

        [Test]
        public async Task Static_only_candidates_are_ranked_by_reputation()
        {
            Node lowReputationNode = new(TestItem.PublicKeyB, "52.141.78.53", 30303, isStatic: true);
            Node highReputationNode = new(TestItem.PublicKeyC, "52.141.78.54", 30303, isStatic: true);
            INodeStats lowReputationStats = Substitute.For<INodeStats>();
            lowReputationStats.CurrentNodeReputation(Arg.Any<DateTime>()).Returns(10);
            INodeStats highReputationStats = Substitute.For<INodeStats>();
            highReputationStats.CurrentNodeReputation(Arg.Any<DateTime>()).Returns(100);

            Peer lowReputationPeer = new(lowReputationNode, lowReputationStats) { IsAwaitingConnection = true };
            Peer highReputationPeer = new(highReputationNode, highReputationStats) { IsAwaitingConnection = true };

            ConcurrentQueue<Node> dialed = new();
            IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
            rlpxHost.ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>()).Returns(true);
            rlpxHost.ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
            {
                dialed.Enqueue(callInfo.Arg<Node>());
                return Task.FromResult(false);
            });

            PeerManager manager = CreatePeerManager(rlpxHost, maxActivePeers: 2,
                peers: [lowReputationPeer, highReputationPeer], staticPeers: [lowReputationPeer, highReputationPeer]);

            manager.Start();
            try
            {
                Assert.That(() => dialed.Count, Is.GreaterThanOrEqualTo(2).After(5000, 10));
            }
            finally
            {
                await manager.StopAsync();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(dialed.Take(2), Is.EqualTo(new[] { highReputationNode, lowReputationNode }));
                Assert.That(highReputationNode.CurrentReputation, Is.EqualTo(100));
                Assert.That(lowReputationNode.CurrentReputation, Is.EqualTo(10));
            }
        }

        [Test]
        public async Task Idle_static_peer_is_contacted_and_dialed_once_alongside_other_candidates([Values] bool staticPeerAcceptsDial)
        {
            Node staticNode = new(TestItem.PublicKeyB, "52.141.78.53", 30303, isStatic: true);
            Node otherNode = new(TestItem.PublicKeyC, "52.141.78.54", 30303);
            Peer staticPeer = new(staticNode, Substitute.For<INodeStats>());
            Peer otherPeer = new(otherNode, Substitute.For<INodeStats>());

            int staticContactChecks = 0;
            int staticDials = 0;
            TaskCompletionSource<(int ContactChecks, int Dials)> otherDialed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IRlpxHost rlpxHost = Substitute.For<IRlpxHost>();
            rlpxHost.ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>()).Returns(callInfo =>
            {
                if (callInfo.Arg<IPAddress>().Equals(staticNode.Address.Address)) Interlocked.Increment(ref staticContactChecks);
                return true;
            });
            rlpxHost.ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
            {
                Node dialed = callInfo.Arg<Node>();
                if (dialed == staticNode)
                {
                    Interlocked.Increment(ref staticDials);
                    return Task.FromResult(staticPeerAcceptsDial);
                }

                if (dialed == otherNode) otherDialed.TrySetResult((Volatile.Read(ref staticContactChecks), Volatile.Read(ref staticDials)));
                return Task.FromResult(true);
            });

            PeerManager manager = CreatePeerManager(rlpxHost, maxActivePeers: 4,
                peers: [staticPeer, otherPeer], staticPeers: [staticPeer]);

            manager.Start();
            (int ContactChecks, int Dials) staticBeforeOtherDial;
            try
            {
                staticBeforeOtherDial = await otherDialed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await manager.StopAsync();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(staticBeforeOtherDial.ContactChecks, Is.EqualTo(1));
                Assert.That(staticBeforeOtherDial.Dials, Is.EqualTo(1));
            }
        }

        private static PeerManager CreatePeerManager(IRlpxHost rlpxHost, int maxActivePeers, Peer[] peers, Peer[] staticPeers)
        {
            ConcurrentDictionary<PublicKeyAsKey, Peer> pooledPeers = new();
            foreach (Peer peer in peers)
            {
                pooledPeers.TryAdd(peer.Node.Id, peer);
            }

            IPeerPool peerPool = Substitute.For<IPeerPool>();
            peerPool.Peers.Returns(pooledPeers);
            peerPool.ActivePeers.Returns(new ConcurrentDictionary<PublicKeyAsKey, Peer>());
            peerPool.StaticPeers.Returns(staticPeers);

            NetworkConfig config = new() { MaxActivePeers = maxActivePeers, NumConcurrentOutgoingConnects = 1, PeersUpdateInterval = 10 };
            return new PeerManager(rlpxHost, peerPool, Substitute.For<INodeStatsManager>(), config,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance);
        }

        [Test]
        public async Task Start_rejects_non_positive_peer_update_interval([Values(0, -1)] int interval)
        {
            await using Context ctx = new();
            ctx.NetworkConfig.PeersUpdateInterval = interval;

            InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(
                () => ctx.PeerManager.Start())!;

            Assert.That(exception.ExitCode, Is.EqualTo(ExitCodes.ForbiddenOptionValue));
        }

        [Test]
        public async Task Periodic_peer_update_continues_after_no_candidate_iterations()
        {
            const int expectedSelectionCount = 3;
            int selectionCount = 0;
            TaskCompletionSource thirdSelection = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
            IPeerPool peerPool = Substitute.For<IPeerPool>();
            peerPool.Peers.Returns(_ =>
            {
                if (Interlocked.Increment(ref selectionCount) == expectedSelectionCount)
                {
                    thirdSelection.TrySetResult();
                }

                return peers;
            });
            peerPool.ActivePeers.Returns(new ConcurrentDictionary<PublicKeyAsKey, Peer>());
            peerPool.StaticPeers.Returns(Array.Empty<Peer>());

            INetworkConfig networkConfig = Substitute.For<INetworkConfig>();
            networkConfig.MaxActivePeers.Returns(1);
            networkConfig.NumConcurrentOutgoingConnects.Returns(1);
            networkConfig.MaxOutgoingConnectPerSec.Returns(20);
            networkConfig.PeersUpdateInterval.Returns(10);

            PeerManager peerManager = new(
                Substitute.For<IRlpxHost>(),
                peerPool,
                Substitute.For<INodeStatsManager>(),
                networkConfig,
                Substitute.For<IEnode>(),
                LimboLogs.Instance);

            peerManager.Start();
            try
            {
                await thirdSelection.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(Volatile.Read(ref selectionCount), Is.GreaterThanOrEqualTo(expectedSelectionCount));
            }
            finally
            {
                await peerManager.StopAsync();
            }
        }

        [Test]
        public async Task Will_ignore_sessions_created_after_stop()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();

            await ctx.PeerManager.StopAsync();
            ctx.RlpxPeer.CreateRandomIncoming();

            Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(0));
        }

        [TestCase(0, 0, TestName = "Stop completes cleanly while idle")]
        [TestCase(1, 5, TestName = "Stop completes when slots are saturated")]
        [CancelAfter(10_000)]
        public async Task Stop_does_not_hang(int maxActivePeers, int persistedPeers)
        {
            await using Context ctx = maxActivePeers > 0 ? new(maxActivePeers: maxActivePeers) : new();
            ctx.SetupPersistedPeers(persistedPeers);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            // Let the loop settle into its wait state
            await Task.Delay(_delayLong);

            Task stopTask = ctx.PeerManager.StopAsync();
            bool completed = await Task.WhenAny(stopTask, Task.Delay(5000)) == stopTask;
            Assert.That(completed, Is.True, "StopAsync should complete within 5 seconds");
            await stopTask;
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task Stop_handles_plain_operation_canceled_exception_from_connect_worker()
        {
            InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
            underlyingLogger.IsError.Returns(true);
            ILogger logger = new(underlyingLogger);
            ILogManager logManager = Substitute.For<ILogManager>();
            logManager.GetClassLogger<PeerManager>().Returns(logger);
            await using Context ctx = new(parallelism: 1, maxActivePeers: 1, peerManagerLogManager: logManager);
            ctx.RlpxPeer.ThrowPlainOperationCanceledOnCancellation();
            ctx.SetupPersistedPeers(1);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(5));

            await ctx.PeerManager.StopAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.PlainOperationCanceledCount, Is.EqualTo(1));
                underlyingLogger.DidNotReceive().Error(
                    Arg.Is<string>(message => message.Contains("Error setting up connection")),
                    Arg.Any<Exception?>());
            }
        }

        [Test]
        public async Task Disconnect_triggers_refill_without_blocking()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(25, TimeSpan.FromSeconds(30));
            Assert.That(ctx.PeerPool.ActivePeers.Count, Is.AtLeast(25));

            int connectsBefore = ctx.RlpxPeer.ConnectAsyncCallsCount;
            ctx.DisconnectAllSessions();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(connectsBefore + 1, TimeSpan.FromSeconds(30));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.GreaterThan(connectsBefore));
        }

        [Test]
        public async Task No_slot_available_before_deadline_does_not_deadlock_refill()
        {
            await using Context ctx = new(maxActivePeers: 1);
            ctx.NetworkConfig.ConnectTimeoutMs = 0;
            ctx.SetupPersistedPeers(5);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(30));
            Assert.That(ctx.PeerPool.ActivePeers.Count, Is.GreaterThan(0));

            await Task.Delay(Nethermind.Network.Timeouts.Handshake + TimeSpan.FromMilliseconds(_delay));

            int connectsBefore = ctx.RlpxPeer.ConnectAsyncCallsCount;
            ctx.DisconnectAllSessions();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(connectsBefore + 1, TimeSpan.FromSeconds(30));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.GreaterThan(connectsBefore));
        }

        private const string enode1String =
            "enode://22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222@51.141.78.53:30303";

        private const string enode2String =
            "enode://1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111b@52.141.78.53:30303";

        private const string enode3String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@some.url";

        private const string enode4String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@some.url:434";

        private const string enode5String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53";

        private const string enode6String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345";

        private const string enode7String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345?discport=6789";

        private const string enode8String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345?somethingWrong=6789";

        private const string enode9String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345?discport=6789?discport=67899";

        private const string enode10String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345:discport=6789";

        [Test]
        public async Task Will_connect_to_a_candidate_node()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(1);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(30));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(1));
        }

        /// <remarks>
        /// The numbers are the point of the case: eight workers against two slots, throttled to four dials a
        /// second, so every worker dequeues a candidate and suspends in the rate limiter with its slot claimed
        /// but not yet active. That is the window a plain capacity check cannot see - reverting the claim dials
        /// five or six against a cap of two, on every run.
        /// </remarks>
        [Test]
        public async Task Will_only_connect_up_to_max_peers()
        {
            const int candidateCount = 50;
            const int maxActivePeers = 2;

            await using Context ctx = new(parallelism: 8, maxActivePeers: maxActivePeers);
            ctx.NetworkConfig.MaxOutgoingConnectPerSec = 4;
            ctx.CreatePeerManager();
            ctx.SetupPersistedPeers(candidateCount);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(maxActivePeers, TimeSpan.FromSeconds(30));
            await Task.Delay(_delayLong);

            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(maxActivePeers));
        }

        [Test]
        public async Task Will_release_the_claimed_slot_when_the_candidate_is_already_active()
        {
            await using Context ctx = new(maxActivePeers: 2);
            ctx.PeerManager.Start();

            // An IN session for the same node can be initialized while the OUT attempt is in the rate
            // limiter, so AddActivePeer refuses it - and the slot it claimed has to go back.
            Node contested = new(new PrivateKeyGenerator().Generate().PublicKey, "1.2.3.4", 30303);
            ctx.PeerPool.ActivePeers[contested.Id] = new Peer(contested);
            ctx.PeerPool.GetOrAdd(contested);
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero, "an already active node was dialed again");

            ctx.PeerPool.GetOrAdd(new Node(new PrivateKeyGenerator().Generate().PublicKey, "1.2.3.5", 30303));

            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(1), "the claim was not released, so the freed slot is unusable");
        }

        /// <remarks>
        /// Contacting a candidate spends an entry in the production recent-IP filter, which then suppresses
        /// it for five minutes. A candidate that loses the last slot before it can claim one was never
        /// dialed, so it must not pay that price - it is offered again as soon as capacity returns, and that
        /// offer has to reach the dial.
        /// </remarks>
        [Test]
        public async Task Will_not_consume_the_contact_filter_when_the_slot_is_lost_before_the_claim()
        {
            const int maxActivePeers = 3;

            await using Context ctx = new(maxActivePeers: maxActivePeers);
            ctx.RlpxPeer.UseRecentIpFilter();

            // Port 0 keeps the candidate out of the update loop's selection, leaving the peer-added fast
            // path as the only way it is dialed - which is what makes the interleaving below reproducible.
            Node refused = new(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.1", 0);

            // CanConnectToPeer is the only step between the free-slot check and the claim, so taking the
            // slots from its stats lookup lands the candidate inside the window the claim has to close.
            bool filled = false;
            ctx.Stats = ForwardingStats(ctx.Stats, node =>
            {
                if (filled || node.Id != refused.Id) return;

                PrivateKeyGenerator keyGenerator = new();
                while (ctx.PeerPool.ActivePeers.Count < maxActivePeers)
                {
                    PublicKey key = keyGenerator.Generate().PublicKey;
                    ctx.PeerPool.ActivePeers[key] = new Peer(new Node(key, "203.0.113.2", 30303));
                }

                filled = true;
            });
            ctx.CreatePeerManager();
            ctx.PeerManager.Start();

            ctx.PeerPool.GetOrAdd(refused);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(filled, Is.True, "the candidate never reached the claim");
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero, "dialed with no slot left");
                Assert.That(ctx.RlpxPeer.ContactedIps, Is.Empty, "a filter entry was spent on a dial that never happened");
            }

            // Capacity is back, so the retry has to get through - a spent entry would hold it off for the
            // next five minutes.
            ctx.PeerPool.ActivePeers.Clear();
            ctx.PeerPool.TryRemove(refused.Id, out _);
            ctx.PeerPool.GetOrAdd(refused);

            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(1), "the refused candidate stayed suppressed");
        }

        [Test]
        public async Task Will_discard_a_duplicate_incoming_session()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            Session session1 = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            Session session2 = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId = TestItem.PublicKeyB;
            session2.RemoteHost = "1.2.3.4";
            session2.RemotePort = 12345;
            session2.RemoteNodeId = TestItem.PublicKeyB;

            ctx.RlpxPeer.CreateIncoming(session1, session2);
            Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(1));
        }

        [Test]
        [NonParallelizable]
        public async Task Will_remove_incoming_session_disconnected_before_handshake([Values] bool diagTracerEnabled)
        {
            bool wasDiagTracerEnabled = NetworkDiagTracer.IsEnabled;
            NetworkDiagTracer.IsEnabled = diagTracerEnabled;
            try
            {
                await AssertIncomingSessionRemovedWhenDisconnectedBeforeHandshake();
            }
            finally
            {
                NetworkDiagTracer.IsEnabled = wasDiagTracerEnabled;
            }
        }

        [Test]
        public void Will_initiate_disconnect_of_incoming_session_before_handshake()
        {
            Session session = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
            {
                RemoteHost = "1.2.3.4",
                RemotePort = 12345
            };

            Assert.DoesNotThrow(() => session.InitiateDisconnect(DisconnectReason.TooManyPeers, "test"));
        }

        private static async Task AssertIncomingSessionRemovedWhenDisconnectedBeforeHandshake()
        {
            await using Context ctx = new();
            _ = new ProtocolsManager(
                Substitute.For<ISyncPeerPool>(),
                Substitute.For<ITxPool>(),
                ctx.DiscoveryApp,
                ctx.RlpxPeer,
                ctx.Stats,
                Substitute.For<IProtocolValidator>(),
                ctx.PeerManager,
                ctx.Storage,
                [],
                [],
                LimboLogs.Instance);
            ctx.PeerManager.Start();
            Session session = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
            {
                RemoteHost = "1.2.3.4",
                RemotePort = 12345
            };

            ctx.RlpxPeer.CreateIncomingBeforeHandshake(session);
            IDictionary sessions = GetSessions(ctx.PeerManager);
            Assert.That(sessions.Contains(session), Is.True);

            session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "test");

            Assert.That(sessions, Is.Empty);
        }

        private static IDictionary GetSessions(PeerManager peerManager) =>
            (IDictionary)typeof(PeerManager)
                .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(peerManager)!;

        [Test]
        public async Task Will_replace_channel_closed_incoming_session_before_delayed_disconnect()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();

            Session firstSession = CreateIncomingSessionTemplate(TestItem.PublicKeyB);
            Session secondSession = CreateIncomingSessionTemplate(TestItem.PublicKeyB);

            ctx.RlpxPeer.CreateIncoming(firstSession);
            Session staleSession = ctx.Sessions.Single();
            staleSession.MarkChannelClosed();

            ctx.RlpxPeer.CreateIncoming(secondSession);
            Session freshSession = ctx.Sessions.Last();

            InitSession(freshSession);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(freshSession.State, Is.EqualTo(SessionState.Initialized));
                Assert.That(ctx.PeerManager.ActivePeers.Single().InSession, Is.SameAs(freshSession));
            }

            staleSession.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "channel disconnected");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(freshSession.State, Is.EqualTo(SessionState.Initialized));
                Assert.That(ctx.PeerManager.ActivePeers.Single().InSession, Is.SameAs(freshSession));
            }
        }

        [Test]
        public async Task Incoming_session_without_hello_does_not_take_the_claimed_identity()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();

            ctx.RlpxPeer.CreateIncoming(receiveHello: false, CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session unproven = ctx.Sessions.Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Is.Empty);
                Assert.That(ctx.PeerPool.TryGet(TestItem.PublicKeyB, out _), Is.False);
            }

            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session genuine = ctx.Sessions.Last();
            InitSession(unproven);
            InitSession(genuine);
            unproven.MarkDisconnected(DisconnectReason.BreachOfProtocol, DisconnectType.Local, "invalid frame MAC");

            AssertGenuineSessionKept(ctx, genuine);
        }

        [Test]
        public async Task Incoming_session_without_hello_does_not_displace_the_connected_peer([Values] ConnectionDirection genuineDirection)
        {
            // This node keeps the IN direction for PublicKeyB, so an unproven IN claim would otherwise replace an OUT session.
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            Session template = CreateIncomingSessionTemplate(TestItem.PublicKeyB);

            if (genuineDirection == ConnectionDirection.In)
            {
                ctx.RlpxPeer.CreateIncoming(template);
            }
            else
            {
                ctx.RlpxPeer.SessionCreated += HandshakeOnCreate;
                ctx.PeerPool.GetOrAdd(template.Node);
                Assert.That(() => ctx.PeerManager.ActivePeers.SingleOrDefault()?.OutSession,
                    Is.Not.Null.After(_delayLonger, 20), "peer manager did not establish the OUT session");
                ctx.RlpxPeer.SessionCreated -= HandshakeOnCreate;
            }

            Session genuine = ctx.Sessions.Single();
            ctx.RlpxPeer.CreateIncoming(receiveHello: false, template);
            Session unproven = ctx.Sessions.Last();
            InitSession(genuine);
            InitSession(unproven);
            unproven.MarkDisconnected(DisconnectReason.BreachOfProtocol, DisconnectType.Local, "invalid frame MAC");

            AssertGenuineSessionKept(ctx, genuine);
        }

        [Test]
        public async Task Privileged_incoming_peer_keeps_its_disconnect_exemption_during_hello_validation([Values] bool isStatic)
        {
            await using Context ctx = new(maxActivePeers: 0);
            ProtocolsManagerTests.Context protocols = new(rlpxHost: ctx.RlpxPeer, peerManager: ctx.PeerManager, clientIdMatcher: "^allowed$");
            ctx.PeerManager.Start();
            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyB, "1.2.3.4", 12345) { IsStatic = isStatic, IsTrusted = !isStatic });

            ctx.RlpxPeer.CreateIncoming(receiveHello: false, CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session session = ctx.Sessions.Last();
            InitSession(session);
            Assert.That(session.Node.IsStatic || session.Node.IsTrusted, Is.False, "a claimed key must not grant privileges before its Hello");
            protocols.ReceiveHello(session);
            Assert.That(session.Node.IsStatic, Is.EqualTo(isStatic));

            AssertGenuineSessionKept(ctx, session);
        }

        [Test]
        public async Task Privileged_incoming_peer_is_recognised_when_its_hello_reaches_the_peer_manager_directly([Values] bool isStatic)
        {
            await using Context ctx = new(maxActivePeers: 0);
            ctx.PeerManager.Start();
            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyB, "1.2.3.4", 12345) { IsStatic = isStatic, IsTrusted = !isStatic });

            ctx.RlpxPeer.CreateIncoming(receiveHello: false, CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session session = ctx.Sessions.Last();
            InitSession(session);
            ctx.PeerManager.OnP2PProtocolInitialized(session);

            AssertGenuineSessionKept(ctx, session);
        }

        [Test]
        public async Task Disconnect_before_handshake_notification_does_not_create_a_peer()
        {
            await using Context ctx = new();
            ctx.RlpxPeer.SessionCreated += (_, e) => e.Session.HandshakeComplete += (_, _) =>
                e.Session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");
            ctx.PeerManager.Start();
            ctx.RlpxPeer.CreateIncoming(receiveHello: false, CreateIncomingSessionTemplate(TestItem.PublicKeyB));

            Assert.That(ctx.PeerPool.TryGet(TestItem.PublicKeyB, out _), Is.False);
        }

        [Test]
        public async Task Hello_after_disconnect_does_not_create_a_peer()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            ctx.RlpxPeer.CreateIncoming(receiveHello: false, CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session session = ctx.Sessions.Single();
            session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");

            ctx.PeerManager.OnP2PProtocolInitialized(session);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Is.Empty);
                Assert.That(ctx.PeerPool.TryGet(TestItem.PublicKeyB, out _), Is.False);
            }
        }

        [Test]
        public async Task Hello_over_the_hard_limit_does_not_activate_the_peer()
        {
            await using Context ctx = new(maxActivePeers: 1);
            ctx.PeerManager.Start();
            PrivateKeyGenerator keyGenerator = new();
            for (int i = 0; i < 11; i++)
            {
                PublicKey key = keyGenerator.Generate().PublicKey;
                ctx.PeerPool.ActivePeers[key] = new Peer(new Node(key, "203.0.113.2", 30303));
            }

            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session session = ctx.Sessions.Single();
            InitSession(session);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.IsClosing, Is.True);
                Assert.That(ctx.PeerPool.ActivePeers.ContainsKey(TestItem.PublicKeyB), Is.False);
            }
        }

        [Test]
        public async Task Pending_inbound_limit_admits_one_session_of_a_privileged_peer([Values] bool isStatic)
        {
            await using Context ctx = new(maxActivePeers: 0);
            ctx.PeerManager.Start();
            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyC, "1.2.3.5", 12345) { IsStatic = isStatic, IsTrusted = !isStatic });
            for (int i = 0; i < 10; i++) ctx.RlpxPeer.CreateIncoming(receiveHello: false, CreateIncomingSessionTemplate(TestItem.PublicKeyB));

            Session template = CreateIncomingSessionTemplate(TestItem.PublicKeyC);
            ctx.RlpxPeer.CreateIncoming(receiveHello: false, template);
            Session privileged = ctx.Sessions.Last();
            ctx.RlpxPeer.CreateIncoming(receiveHello: false, template);
            Session secondClaim = ctx.Sessions.Last();
            InitSession(privileged);
            InitSession(secondClaim);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(privileged.IsClosing, Is.False);
                Assert.That(secondClaim.IsClosing, Is.True);
            }
        }

        [Test]
        public async Task Pending_inbound_limit_releases_capacity_on_hello_or_disconnect([Values] bool receiveHello)
        {
            await using Context ctx = new(maxActivePeers: 1);
            ctx.Stats = Substitute.For<INodeStatsManager>();
            ctx.CreatePeerManager();
            ctx.PeerManager.Start();
            Session template = CreateIncomingSessionTemplate(TestItem.PublicKeyB);
            for (int i = 0; i < 11; i++) ctx.RlpxPeer.CreateIncoming(receiveHello: false, template);

            ctx.RlpxPeer.CreateIncoming(receiveHello: false, template);
            Session overflow = ctx.Sessions.Last();
            InitSession(overflow);
            Assert.That(overflow.IsClosing, Is.True);
            ctx.Stats.DidNotReceive().GetOrAdd(Arg.Any<Node>());

            Session pending = ctx.Sessions[0];
            if (receiveHello)
                ctx.PeerManager.OnP2PProtocolInitialized(pending);
            else
                pending.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");

            ctx.RlpxPeer.CreateIncoming(receiveHello: false, template);
            Session next = ctx.Sessions.Last();
            InitSession(next);
            Assert.That(next.IsClosing, Is.False);
        }

        private static void AssertGenuineSessionKept(Context ctx, Session genuine)
        {
            Peer peer = ctx.PeerManager.ActivePeers.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(genuine.Direction == ConnectionDirection.In ? peer.InSession : peer.OutSession, Is.SameAs(genuine));
                Assert.That(genuine.IsClosing, Is.False);
                Assert.That(ctx.Stats.IsConnectionDelayed(genuine.Node).DelayReason,
                    Is.Not.AnyOf(NodeStatsEventType.Disconnect, NodeStatsEventType.LocalDisconnectDelay),
                    "the unproven session's disconnect was recorded against the claimed identity");
            }
        }

        internal static Session CreateIncomingSessionTemplate(PublicKey remoteNodeId)
        {
            Session session = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.RemoteHost = "1.2.3.4";
            session.RemotePort = 12345;
            session.RemoteNodeId = remoteNodeId;
            return session;
        }

        private static void InitSession(Session session)
        {
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();

            if (session.State == SessionState.HandshakeComplete)
                session.Init(5, context, Substitute.For<IPacketSender>());
        }

        [Test]
        public async Task Rejected_authenticated_inbound_disconnect_does_not_insert_candidate()
        {
            await using Context ctx = new(maxActivePeers: 30);
            ctx.PeerManager.Start();
            for (int i = 0; i < 40; i++)
            {
                Node node = new(new PrivateKeyGenerator().Generate().PublicKey, $"10.0.0.{i + 1}", 30303);
                ctx.PeerPool.ActivePeers.TryAdd(node.Id, new Peer(node));
            }
            ctx.RlpxPeer.AutoAuthenticateIncoming = false;
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session incoming = ctx.Sessions.Single();
            DisconnectReason? reason = null;
            incoming.Disconnected += (_, args) => reason = args.DisconnectReason;
            ctx.RlpxPeer.AuthenticateIncoming(incoming, null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.True);
                Assert.That(reason, Is.EqualTo(DisconnectReason.HardLimitTooManyPeers));
                Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);
            }
        }

        [Test]
        public async Task Active_inbound_stats_use_reserved_capacity_and_release_on_disconnect([Values(0, 30303)] int port)
        {
            await using Context ctx = new(maxStats: 4);
            foreach (PublicKey key in TestItem.PublicKeys.Skip(1).Take(3)) ctx.PeerPool.GetOrAdd(new Node(key, "8.8.8.8", 30303));
            ctx.PeerManager.Start();
            ctx.RlpxPeer.AutoAuthenticateIncoming = false;

            Session incoming = Connect(TestItem.PublicKeyE);
            Assert.That(incoming.IsClosing, Is.False);
            Peer active = ctx.PeerManager.ActivePeers.Single(peer => peer.Node.Id == incoming.Node.Id);
            Assert.That(ctx.Stats.GetOrAdd(incoming.Node), Is.SameAs(active.Stats));
            Session sessionOnly = Connect(TestItem.PublicKeyF);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sessionOnly.IsClosing, Is.False);
                Assert.That(ctx.PeerPool.Peers.ContainsKey(sessionOnly.Node.Id), Is.False);
                Assert.That(ctx.Stats.GetStatsOrNull(sessionOnly.Node), Is.Null);
            }

            incoming.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");
            Session recovered = Connect(TestItem.PublicKeys.ElementAt(6));
            Assert.That(recovered.IsClosing, Is.False);
            Peer recoveredPeer = ctx.PeerManager.ActivePeers.Single(peer => peer.Node.Id == recovered.Node.Id);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recoveredPeer.InSession, Is.SameAs(recovered));
                Assert.That(ctx.Stats.GetOrAdd(recovered.Node), Is.SameAs(recoveredPeer.Stats));
                Assert.That(ctx.Stats.GetStatsOrNull(sessionOnly.Node), Is.Null);
            }

            Session Connect(PublicKey key)
            {
                ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(key));
                Session session;
                lock (ctx.Sessions) session = ctx.Sessions.Single(candidate => candidate.RemoteNodeId == key && candidate.Direction == ConnectionDirection.In);
                session.Node.Port = port;
                ctx.RlpxPeer.AuthenticateIncoming(session, null);
                return session;
            }
        }

        [Test]
        public async Task Zero_port_inbound_is_active_without_becoming_a_candidate()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            ctx.RlpxPeer.AutoAuthenticateIncoming = false;
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session incoming = ctx.Sessions.Single();
            incoming.Node.Port = 0;
            ctx.RlpxPeer.AuthenticateIncoming(incoming, null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers.Single().InSession, Is.SameAs(incoming));
                Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);
            }
            incoming.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Is.Empty);
                Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);
            }
        }

        [Test]
        public async Task Rejected_inbound_churn_does_not_throttle_discovery()
        {
            await using Context ctx = new(maxActivePeers: 3);
            ctx.NetworkConfig.MaxCandidatePeerCount = 1;
            ctx.PeerManager.Start();
            for (int i = 0; i < 20; i++)
            {
                Session template = CreateIncomingSessionTemplate(new PrivateKeyGenerator().Generate().PublicKey);
                ctx.Stats.ReportFailedValidation(template.Node, CompatibilityValidationType.NetworkId);
                ctx.RlpxPeer.CreateIncoming(template);
            }
            Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);
            ctx.PeerPool.Start();
            ctx.DiscoverNew(1);
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(2));
            Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Privileged_inbound_is_exempt_from_candidate_ip_quota([Values] bool trusted)
        {
            await using Context ctx = new(maxActivePeers: 0);
            for (int i = 0; i < 16; i++)
                ctx.PeerPool.GetOrAdd(new Node(new PrivateKeyGenerator().Generate().PublicKey, "1.2.3.4", 30303));
            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyB, "1.2.3.4", 30303) { IsStatic = !trusted, IsTrusted = trusted });
            ctx.PeerManager.Start();
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Last().IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers.Single().Node.Id, Is.EqualTo(TestItem.PublicKeyB));
            }
        }

        [Test]
        public async Task Outbound_useful_response_proof_requires_accepted_hello()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            Node node = new(TestItem.PublicKeyB, "1.2.3.4", 30303);
            ctx.PeerPool.GetOrAdd(node);
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromMilliseconds(_delayLonger));
            Session outbound = ctx.Sessions.Single();
            outbound.Handshake(node.Id);
            InitSession(outbound);
            outbound.MarkUsefulSyncResponse();
            Assert.That(outbound.HasUsefulSyncResponse, Is.False);

            ctx.AcceptHello(outbound);
            outbound.MarkUsefulSyncResponse();

            Assert.That(outbound.HasUsefulSyncResponse, Is.True);
        }

        [Test]
        public async Task Claimed_inbound_identity_cannot_replace_outbound_peer([Values] bool isStatic, [Values] bool isTrusted)
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            Node victim = new(TestItem.PublicKeyB, "1.2.3.4", 30303) { IsStatic = isStatic, IsTrusted = isTrusted };
            ctx.PeerPool.GetOrAdd(victim);
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromMilliseconds(_delayLonger));
            Session outbound = ctx.Sessions.Single();
            outbound.Handshake(victim.Id);
            InitSession(outbound);
            ctx.AcceptHello(outbound);
            INodeStats stats = ctx.Stats.GetOrAdd(victim);
            long reputation = stats.CurrentNodeReputation(DateTime.UtcNow);

            ctx.RlpxPeer.AutoAuthenticateIncoming = false;
            ctx.RlpxPeer.CompleteInboundHandshake = CompleteInboundTransportHandshake;
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(victim.Id));
            Session forged = ctx.Sessions.Last();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(forged.IsClosing, Is.True);
                Assert.That(outbound.IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers.Single().OutSession, Is.SameAs(outbound));
                Assert.That(forged.Node.IsStatic, Is.False);
                Assert.That(forged.Node.IsTrusted, Is.False);
                Assert.That(stats.CurrentNodeReputation(DateTime.UtcNow), Is.EqualTo(reputation));
                Assert.That(stats.DidEventHappen(NodeStatsEventType.Disconnect), Is.False);
            }
        }

        [Test]
        public async Task Inbound_transport_initialization_does_not_admit_claimed_identity(
            [Values(DisconnectReason.Exception, DisconnectReason.ReceiveMessageTimeout)] DisconnectReason reason)
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            ctx.RlpxPeer.AutoAuthenticateIncoming = false;
            ctx.RlpxPeer.CompleteInboundHandshake = CompleteInboundTransportHandshake;
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session incoming = ctx.Sessions.Single();
            InitSession(incoming);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.State, Is.EqualTo(SessionState.Initialized));
                Assert.That(ctx.PeerManager.ActivePeers, Is.Empty);
                Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);
            }
            incoming.MarkDisconnected(reason, DisconnectType.Local, "before authenticated Hello");
            Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);
        }

        private static void CompleteInboundTransportHandshake(Session session, PublicKey claimedIdentity)
        {
            using PooledBufferLeakDetector detector = new();
            IHandshakeService handshakeService = Substitute.For<IHandshakeService>();
            handshakeService.Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>()).Returns(call =>
            {
                EncryptionHandshake handshake = call.Arg<EncryptionHandshake>();
                handshake.RemoteNodeId = claimedIdentity;
                handshake.Secrets = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
                return new Packet([]);
            });
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            context.Allocator.Returns(detector.Allocator);
            context.WriteAndFlushAsync(Arg.Any<object>()).Returns(call =>
            {
                ((IByteBuffer)call.Arg<object>()).Release();
                return Task.CompletedTask;
            });
            NettyHandshakeHandler handler = new(new MessageSerializationService(), handshakeService, session,
                HandshakeRole.Recipient, LimboLogs.Instance, Substitute.For<IEventExecutorGroup>(), TimeSpan.Zero);
            handler.ChannelRead(context, detector.Allocator.Buffer(0, 0));
        }

        [Test]
        public async Task Claimed_privileged_identity_cannot_suppress_first_frame_mac_failure([Values] bool trusted)
        {
            await using Context ctx = new(maxActivePeers: 0);
            ctx.PeerManager.Start();
            Node victim = new(TestItem.PublicKeyB, "1.2.3.4", 30303) { IsStatic = !trusted, IsTrusted = trusted };
            INodeStats stats = ctx.Stats.GetOrAdd(victim);
            ctx.PeerPool.Peers[victim.Id] = new Peer(victim, stats);
            ctx.RlpxPeer.AutoAuthenticateIncoming = false;
            ctx.RlpxPeer.CompleteInboundHandshake = CompleteInboundTransportHandshake;
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(victim.Id));
            Session incoming = ctx.Sessions.Last();

            new ZeroNettyP2PHandler(incoming, LimboLogs.Instance).ExceptionCaught(
                Substitute.For<IChannelHandlerContext>(), new CorruptedFrameException("invalid header MAC"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.True);
                Assert.That(incoming.Node.IsStatic || incoming.Node.IsTrusted, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers, Is.Empty);
                Assert.That(ctx.PeerManager.CandidatePeers.Single().Node, Is.SameAs(victim));
                Assert.That(stats.DidEventHappen(NodeStatsEventType.Disconnect), Is.False);
            }
        }

        [Test]
        public async Task Authenticated_hello_rechecks_outbound_conflict_after_transport_init([Values] bool keepInbound)
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            PublicKey identity = keepInbound ? TestItem.PublicKeyB : TestItem.PublicKeyC;
            ctx.RlpxPeer.AutoAuthenticateIncoming = false;
            ctx.RlpxPeer.CompleteInboundHandshake = CompleteInboundTransportHandshake;
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(identity));
            Session incoming = ctx.Sessions.Single();
            Assert.That(ctx.PeerManager.CandidatePeers, Is.Empty);

            ctx.PeerPool.GetOrAdd(new Node(identity, "1.2.3.4", 30303));
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromMilliseconds(_delayLonger));
            Session outbound = ctx.Sessions.Last();
            outbound.Handshake(identity);
            InitSession(outbound);
            ctx.AcceptHello(incoming);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.EqualTo(!keepInbound));
                Assert.That(outbound.IsClosing, Is.EqualTo(keepInbound));
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public async Task Simultaneous_dials_at_both_ends_converge_after_hello([Values] bool leftHelloFirst)
        {
            await using Context left = new(localNodeId: TestItem.PublicKeyA);
            await using Context right = new(localNodeId: TestItem.PublicKeyB);
            left.PeerManager.Start();
            right.PeerManager.Start();
            left.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyB, "1.2.3.4", 30303));
            right.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.5", 30303));
            await left.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(5));
            await right.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(5));
            Session leftOut = left.Sessions.Single();
            Session rightOut = right.Sessions.Single();
            leftOut.Handshake(TestItem.PublicKeyB);
            rightOut.Handshake(TestItem.PublicKeyA);
            InitSession(leftOut);
            InitSession(rightOut);

            foreach (Context ctx in new[] { left, right })
            {
                ctx.RlpxPeer.AutoAuthenticateIncoming = false;
                ctx.RlpxPeer.CompleteInboundHandshake = CompleteInboundTransportHandshake;
                ctx.RlpxPeer.CreateIncoming(ctx.Sessions.Single());
            }
            Session leftIn = left.Sessions.Last();
            Session rightIn = right.Sessions.Last();
            Assert.That(leftIn.IsClosing || rightIn.IsClosing, Is.False, "transport-only cross-dials must reach Hello");
            if (leftHelloFirst)
            {
                left.AcceptHello(leftIn);
                right.AcceptHello(rightIn);
            }
            else
            {
                right.AcceptHello(rightIn);
                left.AcceptHello(leftIn);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(leftIn.IsClosing, Is.EqualTo(rightOut.IsClosing));
                Assert.That(leftOut.IsClosing, Is.EqualTo(rightIn.IsClosing));
                Assert.That(leftIn.IsClosing, Is.Not.EqualTo(leftOut.IsClosing));
            }
        }

        [Test]
        public void Will_return_exception_in_port() =>
            Assert.Throws<ArgumentException>(static () => new Enode(enode3String));

        [Test]
        public void Will_return_exception_in_dns() =>
            Assert.Throws<ArgumentException>(static () => new Enode(enode4String));

        [Test]
        public void Will_return_exception_when_there_is_no_port() =>
            Assert.Throws<ArgumentException>(static () => new Enode(enode5String));

        [Test]
        public void Will_parse_ports_correctly_when_there_are_two_different_ports()
        {
            Enode enode = new(enode6String);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(enode.Port, Is.EqualTo(12345));
                Assert.That(enode.DiscoveryPort, Is.EqualTo(12345));
            }
        }

        [Test]
        public void Will_parse_port_correctly_when_there_is_only_one()
        {
            Enode enode = new(enode7String);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(enode.Port, Is.EqualTo(12345));
                Assert.That(enode.DiscoveryPort, Is.EqualTo(6789));
            }
        }

        [Test]
        public void Will_return_exception_on_wrong_ports_part() =>
            Assert.Throws<ArgumentException>(static () => new Enode(enode8String));

        [Test]
        public void Will_return_exception_on_duplicated_discovery_port_part() =>
            Assert.Throws<ArgumentException>(static () => new Enode(enode9String));

        [Test]
        public void Will_return_exception_on_wrong_form_of_discovery_port_part() =>
            Assert.Throws<ArgumentException>(static () => new Enode(enode10String));
        [Test]
        public async Task Authenticated_privileged_inbound_is_exempt_from_quota([Values] bool trusted)
        {
            await using Context ctx = new(maxActivePeers: 0);
            ctx.PeerManager.Start();
            ctx.CreateNewIncomingSessions(1);
            Assert.That(ctx.Sessions.Single().IsClosing, Is.True);
            Node privileged = new(TestItem.PublicKeyB, "1.2.3.4", 30303) { IsStatic = !trusted, IsTrusted = trusted };
            ctx.PeerPool.Peers[privileged.Id] = new Peer(privileged, ctx.Stats.GetOrAdd(privileged));
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(privileged.Id));
            Session incoming = ctx.Sessions.Last();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.False);
                Assert.That(incoming.Node.IsStatic, Is.EqualTo(!trusted));
                Assert.That(incoming.Node.IsTrusted, Is.EqualTo(trusted));
                Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task MaxActivePeers_is_not_inflated_by_static_or_trusted()
        {
            await using Context ctx = new(maxActivePeers: 20);
            Assert.That(ctx.PeerManager.MaxActivePeers, Is.EqualTo(20));

            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1) { IsStatic = true });
            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyB, "1.2.3.5", 1) { IsTrusted = true });

            Assert.That(ctx.PeerManager.MaxActivePeers, Is.EqualTo(20), "static and trusted peers do not inflate the limit");
        }

        // Migrated from ProtocolValidatorTests: the capacity policy moved into the peer manager,
        // applied when a session completes the P2P Hello exchange.
        [TestCase(11, 10, true)]
        [TestCase(10, 10, false)]
        [TestCase(9, 10, false)]
        public async Task On_max_active_peer_limit(int activePeerCount, int maxActivePeer, bool shouldDisconnect)
        {
            await using Context ctx = new(maxActivePeers: maxActivePeer);
            PrivateKeyGenerator keyGenerator = new();
            for (int i = 0; i < activePeerCount; i++)
            {
                PublicKey key = keyGenerator.Generate().PublicKey;
                ctx.PeerPool.ActivePeers[key] = new Peer(new Node(key, "1.2.3.4", 30303));
            }

            ISession session = Substitute.For<ISession>();
            session.Direction.Returns(ConnectionDirection.Out);
            Peer owner = ctx.PeerPool.ActivePeers.First().Value;
            session.Node.Returns(owner.Node);
            owner.OutSession = session;
            ctx.PeerManager.OnP2PProtocolInitialized(session);

            if (shouldDisconnect)
            {
                session.Received(1).InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
            }
            else
            {
                session.DidNotReceive().InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
            }
        }

        [Test]
        public async Task Capacity_overflow_sheds_inbound_before_authenticated_outbound()
        {
            await using Context ctx = new(maxActivePeers: 1);
            Node incomingNode = new(TestItem.PublicKeyB, "1.2.3.4", 30303);
            ISession incoming = Substitute.For<ISession>();
            incoming.Node.Returns(incomingNode);
            ctx.PeerPool.ActivePeers[incomingNode.Id] = new Peer(incomingNode) { InSession = incoming };
            Node outgoingNode = new(TestItem.PublicKeyC, "1.2.3.5", 30303);
            ISession outgoing = Substitute.For<ISession>();
            outgoing.Node.Returns(outgoingNode);
            outgoing.Direction.Returns(ConnectionDirection.Out);
            ctx.PeerPool.ActivePeers[outgoingNode.Id] = new Peer(outgoingNode) { OutSession = outgoing };

            ctx.PeerManager.OnP2PProtocolInitialized(outgoing);

            incoming.Received(1).InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
            outgoing.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }

        [Test]
        [NonParallelizable]
        public async Task Will_agree_on_which_session_to_disconnect_when_connecting_at_once([Values] bool shouldLose,
            [Values(ConnectionDirection.In, ConnectionDirection.Out)] ConnectionDirection firstDirection)
        {
            await using Context ctx = new();

            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            Session session1 = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);

            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId =
                (firstDirection == ConnectionDirection.In)
                    ? (shouldLose ? TestItem.PublicKeyB : TestItem.PublicKeyC)
                    : (shouldLose ? TestItem.PublicKeyC : TestItem.PublicKeyB);

            bool expectedOutSessionClosing = firstDirection == ConnectionDirection.In ? shouldLose : !shouldLose;

            if (firstDirection == ConnectionDirection.In)
            {
                ctx.RlpxPeer.CreateIncoming(session1);
                if (!await ctx.RlpxPeer.ConnectAsync(session1.Node))
                {
                    throw new NetworkingException($"Failed to connect to {session1.Node:s}", NetworkExceptionType.TargetUnreachable);
                }
            }
            else
            {
                // Dialing rlpx directly would leave the session unowned: the peer manager only keeps an
                // outgoing session for a peer it already made active. Adding the node to the pool makes it
                // dial, which activates the peer and attaches the OUT session before the IN one arrives.
                ctx.RlpxPeer.SessionCreated += HandshakeOnCreate;
                ctx.PeerPool.GetOrAdd(session1.Node);
                Assert.That(() => ctx.PeerManager.ActivePeers.SingleOrDefault()?.OutSession,
                    Is.Not.Null.After(_delayLonger, 20), "peer manager did not establish the OUT session");
                ctx.RlpxPeer.SessionCreated -= HandshakeOnCreate;
                ctx.RlpxPeer.CreateIncoming(session1);
            }

            AssertAgreedOnSessionToDisconnect(ctx, expectedOutSessionClosing);
        }

        [Test]
        [NonParallelizable]
        public async Task Will_agree_on_which_session_to_disconnect_when_incoming_arrives_while_dialing([Values] bool keepIn)
        {
            PublicKey remoteNodeId = keepIn ? TestItem.PublicKeyB : TestItem.PublicKeyC;
            Session incoming = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
            {
                RemoteHost = "1.2.3.4",
                RemotePort = 12345,
                RemoteNodeId = remoteNodeId
            };

            await using Context ctx = new();
            bool injected = false;
            InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
            underlyingLogger.IsTrace.Returns(true);
            // Traced after the dial activates the peer but before it marks the peer as awaiting the connection.
            underlyingLogger
                .When(static logger => logger.Trace(Arg.Is<string>(static text => text.StartsWith("CONNECTING TO"))))
                .Do(_ =>
                {
                    ctx.RlpxPeer.CreateIncoming(incoming);
                    injected = true;
                });
            ILogger logger = new(underlyingLogger);
            ILogManager logManager = Substitute.For<ILogManager>();
            logManager.GetClassLogger<PeerManager>().Returns(logger);
            ctx.CreatePeerManager(logManager);

            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            ctx.TestNodeSource.AddNode(new Node(remoteNodeId, incoming.RemoteHost, incoming.RemotePort));

            // The mock announces the dialed session, and the peer manager attaches it, before the connect is counted.
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromMilliseconds(_delayLonger));
            Assert.That(injected, Is.True, "the incoming session was not injected into the dial window");

            Peer activePeer = ctx.PeerManager.ActivePeers.Single();
            InitializeSessions(activePeer);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(HasAgreedOnSessionToDisconnect(activePeer, expectedOutSessionClosing: keepIn), Is.True);
                Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(1));
            }
        }

        [Test]
        [NonParallelizable]
        public async Task Will_agree_on_which_session_to_disconnect_when_incoming_and_outgoing_attach_concurrently()
        {
            // No hook exists between the attach check and the attach, so the race is run many times over.
            const int attempts = 100;
            await using Context ctx = new(parallelism: 1, maxActivePeers: attempts);
            Session? incoming = null;
            Task incomingAdded = Task.CompletedTask;
            using Barrier start = new(2);
            InterfaceLogger underlyingLogger = Substitute.For<InterfaceLogger>();
            underlyingLogger.IsTrace.Returns(true);
            // Releases the incoming session together with the dial's own session.
            underlyingLogger
                .When(static logger => logger.Trace(Arg.Is<string>(static text => text.StartsWith("CONNECTING TO"))))
                .Do(_ =>
                {
                    Session session = incoming!;
                    Volatile.Write(ref incomingAdded, Task.Run(() =>
                    {
                        start.SignalAndWait();
                        ctx.RlpxPeer.CreateIncoming(session);
                    }));
                    start.SignalAndWait();
                });
            ILogger logger = new(underlyingLogger);
            ILogManager logManager = Substitute.For<ILogManager>();
            logManager.GetClassLogger<PeerManager>().Returns(logger);
            ctx.CreatePeerManager(logManager);

            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                PublicKey remoteNodeId = new PrivateKeyGenerator().Generate().PublicKey;
                incoming = new Session(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
                {
                    RemoteHost = "1.2.3.4",
                    RemotePort = 12345,
                    RemoteNodeId = remoteNodeId
                };
                ctx.TestNodeSource.AddNode(new Node(remoteNodeId, incoming.RemoteHost, incoming.RemotePort));
                await ctx.RlpxPeer.WaitForConnectCallsAsync(attempt + 1, TimeSpan.FromMilliseconds(_delayLonger));
                await Volatile.Read(ref incomingAdded);

                Peer peer = ctx.PeerManager.ActivePeers.Single(p => p.Node.Id == remoteNodeId);
                InitializeSessions(peer);
                Assert.That(peer.OutSession is not null && (peer.InSession?.IsClosing ?? true) != peer.OutSession.IsClosing,
                    Is.True, $"attempt {attempt}: exactly one direction must be disconnected");
            }
        }

        private void AssertAgreedOnSessionToDisconnect(Context ctx, bool expectedOutSessionClosing)
        {
            Assert.That(() =>
            {
                Peer? activePeer = ctx.PeerManager.ActivePeers.SingleOrDefault();
                if (activePeer is null) return false;

                InitializeSessions(activePeer);
                return HasAgreedOnSessionToDisconnect(activePeer, expectedOutSessionClosing);
            }, Is.True.After(_delayLonger, 20));

            Assert.That(() => ctx.PeerManager.ActivePeers.Count, Is.EqualTo(1).After(_delay, 10));
        }

        /// <summary>Completes the handshake and P2P init, which is when a session's deferred disconnect takes effect.</summary>
        private static void InitializeSessions(Peer peer)
        {
            PacketSender packetSender = new(Substitute.For<IMessageSerializationService>(), LimboLogs.Instance, TimeSpan.Zero);
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();

            void EnsureSession(ISession? session)
            {
                if (session is null) return;
                if (session.State < SessionState.HandshakeComplete) session.Handshake(session.Node.Id);
                if (session.State < SessionState.Initialized) session.Init(5, context, packetSender);
            }

            EnsureSession(peer.OutSession);
            EnsureSession(peer.InSession);
        }

        private static bool HasAgreedOnSessionToDisconnect(Peer peer, bool expectedOutSessionClosing) =>
            peer.OutSession is not null
            && peer.OutSession.IsClosing == expectedOutSessionClosing
            && (peer.InSession?.IsClosing ?? true) != expectedOutSessionClosing;

        private void HandshakeOnCreate(object sender, SessionEventArgs e) => e.Session.Handshake(e.Session.RemoteNodeId);

        [Test]
        public async Task Will_not_dial_a_node_carrying_this_nodes_own_identity()
        {
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            ctx.TestNodeSource.AddNode(new Node(TestItem.PublicKeyA, "1.2.3.4", 30303));

            Assert.That(() => ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(0).After(_delay),
                "a discovered node carrying the local identity is a hairpinned self-dial and must never be attempted");
        }

        [Test]
        public async Task Will_not_select_a_static_node_carrying_this_nodes_own_identity()
        {
            await using Context ctx = new();
            ctx.RlpxPeer.MakeItFail();
            const long neverRanked = -424242;
            Node self = new(TestItem.PublicKeyA, "1.2.3.4", 30303) { IsStatic = true, CurrentReputation = neverRanked };
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            ctx.TestNodeSource.AddNode(new Node(new PrivateKeyGenerator().Generate().PublicKey, "5.6.7.8", 30303));
            ctx.TestNodeSource.AddNode(self);
            await Task.Delay(_delayLonger);

            Assert.That(self.CurrentReputation, Is.EqualTo(neverRanked),
                "a static peer bypasses the pre-candidate filters, so the local identity has to be dropped from the static projection too - otherwise it is selected every round, discarded again at dial time without taking a slot, and the update loop never reaches its idle delay");
        }

        [Test]
        public async Task Will_fill_up_on_disconnects()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(25, TimeSpan.FromSeconds(30));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.AtLeast(25));
            ctx.DisconnectAllSessions();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(50, TimeSpan.FromSeconds(30));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.AtLeast(50));
        }

        [Test]
        public async Task Ok_if_fails_to_connect()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.RlpxPeer.MakeItFail();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            // Wait for at least one failed connect attempt so we know the manager has cycled.
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(30));

            // ActivePeers transiently holds a peer between AddActivePeer (before ConnectAsync) and
            // DeactivatePeerIfDisconnected (after ConnectAsync returns false). Sessions, however,
            // is only populated when ConnectAsync actually creates a session — which the failing
            // mock never does. That's the invariant this test is asserting.
            lock (ctx.Sessions) Assert.That(ctx.Sessions, Is.Empty);
        }

        [Test]
        [NonParallelizable]
        public async Task Will_fill_up_over_and_over_again_on_disconnects()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            TimeSpan prevConnectingDelay = StatsParameters.Instance.EventParams[NodeStatsEventType.Connecting].ReconnectDelay;
            StatsParameters.Instance.EventParams[NodeStatsEventType.Connecting] = (TimeSpan.Zero, 0);
            int[] prevDisconnectDelays = StatsParameters.Instance.DisconnectDelays;
            StatsParameters.Instance.DisconnectDelays = new[] { 0 };

            try
            {
                for (int i = 0; i < 10; i++)
                {
                    await ctx.WaitForActivePeersAsync(25, TimeSpan.FromSeconds(60));
                    ctx.DisconnectAllSessions();
                }
            }
            finally
            {
                StatsParameters.Instance.EventParams[NodeStatsEventType.Connecting] = (prevConnectingDelay, 0);
                StatsParameters.Instance.DisconnectDelays = prevDisconnectDelays;
            }
        }

        [Test]
        public async Task Will_fill_up_over_and_over_again_on_newly_discovered()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(0);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                ctx.DiscoverNew(25);
                Assert.That(() => ctx.PeerManager.ActivePeers.Count, Is.EqualTo(25).After(_delayLonger, 10));
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        public async Task Will_not_stop_trying_on_rlpx_connection_failure()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(0);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            ctx.RlpxPeer.MakeItFail();

            for (int i = 0; i < 10; i++)
            {
                ctx.DiscoverNew(25);
                // The manager keeps retrying failed peers in the background, so the counter never
                // settles — assert it reaches each cycle's threshold instead of an exact value.
                await ctx.RlpxPeer.WaitForConnectCallsAsync(25 * (i + 1), TimeSpan.FromSeconds(30));
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.AtLeast(25 * (i + 1)));
            }
        }

        [Test]
        public async Task Will_not_stop_trying_on_any_failure()
        {
            await using Context ctx = new(maxActivePeers: 110);

            ctx.Stats = Substitute.For<INodeStatsManager>();
            ctx.Stats
                .When(it => it.ReportEvent(Arg.Any<Node>(), NodeStatsEventType.ConnectionFailed))
                .Throw(new Exception("test exception"));

            Enumerable.Range(0, 110).ForEach((idx) =>
            {
                PublicKey key = new PrivateKeyGenerator().Generate().PublicKey;
                ctx.PeerPool.GetOrAdd(new Node(key, "1.2.3.4", idx));
            });

            ctx.CreatePeerManager();
            ctx.SetupPersistedPeers(0);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            ctx.RlpxPeer.MakeItFail();

            Assert.That(() => ctx.RlpxPeer.ConnectAsyncCallsCount, Is.GreaterThan(100).After(10000, 10));
            await ctx.PeerManager.StopAsync();
        }

        [Test]
        public async Task Pooled_penalties_prevent_selection_after_stats_pressure([Values] bool reconnectDelay)
        {
            await using Context ctx = new(maxActivePeers: 1, maxStats: 3);
            Node penalized = new(new NetworkNode(ctx.GenerateEnode()));
            Peer peer = ctx.PeerPool.GetOrAdd(penalized);
            peer.Stats!.CurrentPersistedNodeReputation = 1000;
            foreach (PublicKey key in TestItem.PublicKeys.Take(10))
                ctx.Stats.GetOrAdd(new Node(key, "9.9.9.9", 30303));

            if (reconnectDelay) ctx.Stats.ReportDisconnect(penalized, DisconnectType.Remote, DisconnectReason.TooManyPeers);
            else ctx.Stats.ReportFailedValidation(penalized, CompatibilityValidationType.NetworkId);
            Node healthy = new(TestItem.PublicKeyB, "8.8.4.4", 30303);
            ctx.PeerPool.GetOrAdd(healthy);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(3));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Single().Node.Id, Is.EqualTo(healthy.Id));
                Assert.That(ctx.Stats.GetOrAdd(penalized), Is.SameAs(peer.Stats));
            }
        }

        private readonly int _delay = 500;
        // Connections go through the update loop and several thread-pool hops while the fixture's other managers run;
        // the poll returns as soon as the count is reached.
        private readonly int _connectWait = 10_000;

        private readonly int _delayLong = 1000;
        private readonly int _delayLonger = 3000;

        [Test]
        public async Task Inbound_peers_leave_one_third_of_capacity_for_outbound_dials()
        {
            await using Context ctx = new(maxActivePeers: 30);
            ctx.RlpxPeer.MakeItFail();
            ctx.SetupPersistedPeers(0);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            ctx.CreateNewIncomingSessions(21);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(20));
                Assert.That(ctx.Sessions.Last().IsClosing, Is.True);
            }

            int previousDials = ctx.RlpxPeer.ConnectAsyncCallsCount;
            ctx.RlpxPeer.MakeItFail(false);
            ctx.DiscoverNew(10);
            await ctx.WaitForActivePeersAsync(30, TimeSpan.FromSeconds(10));
            await ctx.RlpxPeer.WaitForConnectCallsAsync(previousDials + 10, TimeSpan.FromSeconds(10));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount - previousDials, Is.EqualTo(10));
        }

        [Test]
        [Explicit("CI issues - bad test design")]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            int currentCount = 0;
            int maxCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                maxCount += 50;
                Assert.That(() => ctx.RlpxPeer.ConnectAsyncCallsCount, Is.InRange(currentCount, maxCount).After(_delayLonger * 2, 10));
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.InRange(currentCount, maxCount));
                ctx.HandshakeAllSessions();
                await Task.Delay(_delay);
                ctx.DisconnectAllSessions();
            }

            await ctx.PeerManager.StopAsync();
            ctx.DisconnectAllSessions();

            Assert.That(
                () => ctx.PeerManager.CandidatePeers.All(p => p.OutSession is null),
                Is.True.After(2000, 10));
        }

        [Test]
        [Explicit("CI issues - bad test design")]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40()
        {
            await using Context ctx = new();
            ctx.NetworkConfig.MaxCandidatePeerCount = 40;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 30;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 40;
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            int currentCount = 0;
            int count = 35;
            for (int i = 0; i < 10; i++)
            {
                currentCount += count;
                await Task.Delay(_delayLong);
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.InRange(currentCount, currentCount + count));
                ctx.HandshakeAllSessions();
                await Task.Delay(_delay);
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        [Explicit("CI issues - bad test design")]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40_with_random_incoming_connections()
        {
            await using Context ctx = new();
            ctx.NetworkConfig.MaxCandidatePeerCount = 40;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 30;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 40;
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            int count = 35;
            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += count;
                await Task.Delay(_delayLong);
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.InRange(currentCount, currentCount + count));
                ctx.HandshakeAllSessions();
                await Task.Delay(_delay);
                ctx.CreateIncomingSessions();
                await Task.Delay(_delay);
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        public async Task Will_not_cleanup_active_peers()
        {
            await using Context ctx = new(maxActivePeers: 4);
            ctx.NetworkConfig.PeersUpdateInterval = 10;
            ctx.NetworkConfig.MaxCandidatePeerCount = 4;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 1;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 1;
            foreach (NetworkNode node in ctx.CreateNodes(4, diverseAddresses: true))
            {
                Peer peer = ctx.PeerPool.GetOrAdd(node);
                ctx.PeerPool.ActivePeers[node.NodeId] = peer;
            }
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            PublicKey[] active = ctx.PeerManager.ActivePeers.Select(peer => peer.Node.Id).ToArray();
            Assert.That(active, Has.Length.EqualTo(4));
            TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.PeerPool.PeerRemoved += OnPeerRemoved;
            try
            {
                ctx.NetworkConfig.MaxCandidatePeerCount = 2;
                foreach (NetworkNode node in ctx.CreateNodes(3)) ctx.PeerPool.GetOrAdd(node);
                await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ctx.PeerPool.PeerCount, Is.EqualTo(6));
                    Assert.That(ctx.PeerManager.ActivePeers.Select(peer => peer.Node.Id), Is.EquivalentTo(active));
                }
            }
            finally
            {
                ctx.PeerPool.PeerRemoved -= OnPeerRemoved;
            }

            void OnPeerRemoved(object? sender, PeerEventArgs args) => cleanup.TrySetResult();
        }

        [Test]
        public async Task Will_load_static_nodes_and_connect_to_them()
        {
            await using Context ctx = new();
            const int nodesCount = 5;
            List<NetworkNode> staticNodes = ctx.CreateNodes(nodesCount);
            ctx.StaticNodesManager.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(staticNodes.Select(static n => new Node(n, true)).ToAsyncEnumerable());
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            foreach (NetworkNode node in staticNodes)
            {
                ctx.TestNodeSource.AddNode(new Node(TestItem.PublicKeyA, node.Host, node.Port));
            }

            Assert.That(() => ctx.PeerManager.ActivePeers.Count(static p => p.Node.IsStatic), Is.EqualTo(nodesCount).After(_connectWait, 10));
        }

        [Test]
        public async Task Will_disconnect_on_remove_static_node()
        {
            await using Context ctx = new();
            const int nodesCount = 5;
            int disconnections = 0;
            List<NetworkNode> staticNodes = ctx.CreateNodes(nodesCount);
            ctx.StaticNodesManager.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(staticNodes.Select(n => new Node(n, true)).ToAsyncEnumerable());
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await ctx.RlpxPeer.WaitForConnectCallsAsync(nodesCount, TimeSpan.FromSeconds(30));

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            ctx.Sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            ctx.StaticNodesManager.NodeRemoved += Raise.EventWith<NodeEventArgs>(new ExplicitNodeRemovalEventArgs(
                new Node(staticNodes.First())));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers.Count(p => p.Node.IsStatic), Is.EqualTo(nodesCount - 1));
                Assert.That(disconnections, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task Will_connect_and_disconnect_on_peer_management()
        {
            await using Context ctx = new();
            int disconnections = 0;
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            NetworkNode node = new(ctx.GenerateEnode());
            ctx.PeerPool.GetOrAdd(node);
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(30));

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            Assert.That(ctx.PeerManager.ActivePeers.Select(p => p.Node.Id), Is.EqualTo(new[] { node.NodeId }));

            ctx.Sessions.ForEach(s => s.Disconnected += DisconnectHandler);

#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
            Assert.That(ctx.PeerPool.TryRemove(node.NodeId, out _), Is.True);
#pragma warning restore NUnit2045
            Assert.That(ctx.PeerManager.ActivePeers, Is.Empty);
            Assert.That(disconnections, Is.EqualTo(1));
        }

        [Test]
        public async Task Will_only_add_same_peer_once()
        {
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            NetworkNode node = new(ctx.GenerateEnode());
            ctx.PeerPool.GetOrAdd(node);
            ctx.PeerPool.GetOrAdd(node);
            ctx.PeerPool.GetOrAdd(node);
            await Task.Delay(_delayLong);
            Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task RemovePeer_should_fail_if_peer_not_added()
        {
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            NetworkNode node = new(ctx.GenerateEnode());
            Assert.That(() => ctx.PeerPool.TryRemove(node.NodeId, out _), Is.False.After(_delay, 10));
        }

        /// <summary>
        /// A stats manager that answers from <paramref name="inner"/> and runs <paramref name="onNodeChecked"/>
        /// on every compatibility lookup, the one the dial fast path makes per candidate.
        /// </summary>
        private static INodeStatsManager ForwardingStats(INodeStatsManager inner, Action<Node> onNodeChecked)
        {
            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            stats.GetOrAdd(Arg.Any<Node>()).Returns(call => inner.GetOrAdd(call.Arg<Node>()));
            stats.GetStatsOrNull(Arg.Any<Node>()).Returns(call => inner.GetStatsOrNull(call.Arg<Node>()));
            stats.IsInboundBanned(Arg.Any<Node>(), Arg.Any<DateTime>()).Returns(call => inner.IsInboundBanned(call.Arg<Node>(), call.Arg<DateTime>()));
            stats.TryAcquire(Arg.Any<Node>(), Arg.Any<bool>(), out Arg.Any<INodeStats?>(), out Arg.Any<IDisposable?>()).Returns(call =>
            {
                bool accepted = inner.TryAcquire(call.Arg<Node>(), call.Arg<bool>(), out INodeStats? retained, out IDisposable? lease);
                call[2] = retained;
                call[3] = lease;
                return accepted;
            });
            stats.When(manager => manager.PromoteLease(Arg.Any<IDisposable?>())).Do(call => inner.PromoteLease(call.Arg<IDisposable?>()));
            stats.GetCurrentReputation(Arg.Any<Node>()).Returns(call => inner.GetCurrentReputation(call.Arg<Node>()));
            stats.IsConnectionDelayed(Arg.Any<Node>()).Returns(call => inner.IsConnectionDelayed(call.Arg<Node>()));
            stats.HasFailedValidation(Arg.Any<Node>()).Returns(call => inner.HasFailedValidation(call.Arg<Node>()));
            stats.FindCompatibilityValidationResult(Arg.Any<Node>()).Returns(call =>
            {
                Node node = call.Arg<Node>();
                onNodeChecked(node);
                return inner.FindCompatibilityValidationResult(node);
            });

            return stats;
        }

        internal class Context : IAsyncDisposable
        {
            public RlpxMock RlpxPeer { get; }
            public IDiscoveryApp DiscoveryApp { get; }
            public INodeStatsManager Stats { get; set; }
            public INetworkStorage Storage { get; }
            public NodesLoader NodesLoader { get; }
            public PeerManager PeerManager { get; set; }
            public IPeerPool PeerPool { get; }
            public INetworkConfig NetworkConfig { get; }
            public IStaticNodesManager StaticNodesManager { get; }
            public TestNodeSource TestNodeSource { get; }
            public List<Session> Sessions { get; } = [];
            private readonly TimeProvider _timeProvider;
            private readonly PublicKey _localNodeId;
            private readonly PeerAddressPolicy _addressPolicy;

            public Context(int parallelism = 0, int maxActivePeers = 25, ILogManager? peerManagerLogManager = null, TimeProvider? timeProvider = null, PublicKey? localNodeId = null, int maxStats = 10000)
            {
                _localNodeId = localNodeId ?? TestItem.PublicKeyA;
                _timeProvider = timeProvider ?? TimeProvider.System;
                RlpxPeer = new RlpxMock(Sessions);
                DiscoveryApp = Substitute.For<IDiscoveryApp>();
                DiscoveryApp.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<Node>());
                ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
                Stats = new NodeStatsManager(timerFactory, LimboLogs.Instance, maxStats, _timeProvider);
                Storage = new InMemoryStorage();
                _addressPolicy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback), new LocalAddressSource(LimboLogs.Instance, () => []));
                NodesLoader = new NodesLoader(new NetworkConfig(), Stats, Storage, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance, new NodesLoaderOptions(), _addressPolicy);
                NetworkConfig = new NetworkConfig();
                NetworkConfig.MaxActivePeers = maxActivePeers;
                NetworkConfig.PeersPersistenceInterval = 50;
                NetworkConfig.NumConcurrentOutgoingConnects = parallelism;
                NetworkConfig.MaxOutgoingConnectPerSec = int.MaxValue; // zero interval: a finite rate can throttle a back-to-back dial off the fast path
                StaticNodesManager = Substitute.For<IStaticNodesManager>();
                StaticNodesManager.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<Node>());
                TestNodeSource = new TestNodeSource();
                CompositeNodeSource nodeSources = new(NodesLoader, DiscoveryApp, StaticNodesManager, TestNodeSource);
                ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
                PeerPool = new PeerPool(nodeSources, Stats, Storage, NetworkConfig, LimboLogs.Instance, trustedNodesManager, TimeProvider.System, _addressPolicy);
                CreatePeerManager(peerManagerLogManager);
            }

            public void CreatePeerManager(ILogManager? logManager = null)
            {
                PeerManager = new PeerManager(RlpxPeer, PeerPool, Stats, NetworkConfig, new Enode(_localNodeId, IPAddress.Loopback, 30303), logManager ?? LimboLogs.Instance, _timeProvider);
                RlpxPeer.AuthenticateIncoming = (session, listenPort) =>
                {
                    InitSession(session);
                    AcceptHello(session, listenPort);
                };
            }

            public void AcceptHello(ISession session, int? listenPort = null)
            {
                MessageSerializationService serializer = new(SerializerInfo.Create(new HelloMessageSerializer()));
                using P2PProtocolHandler handler = new(session, new Enode(_localNodeId, IPAddress.Loopback, 30303),
                    Stats, serializer, Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance, new NetworkConfig());
                handler.AddSupportedCapability(new Capability(Protocol.Eth, 66));
                handler.ProtocolInitialized += (_, args) =>
                {
                    if (session.Direction == ConnectionDirection.In) session.Node.Port = ((P2PProtocolInitializedEventArgs)args).ListenPort;
                    PeerManager.OnP2PProtocolInitialized(session);
                };
                using HelloMessage hello = new()
                {
                    NodeId = session.RemoteNodeId,
                    P2PVersion = 5,
                    ClientId = "test peer",
                    ListenPort = listenPort ?? session.Node.Port,
                    Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 66) }
                };
                P2PProtocolHandlerTests.HandleHello(handler, serializer, hello);
            }

            public void SetupPersistedPeers(int count) => Storage.UpdateNodes(CreateNodes(count, diverseAddresses: true));

            public void CreateIncomingSessions()
            {
                Session[] clone;

                lock (Sessions)
                {
                    clone = Sessions.ToArray();
                }

                RlpxPeer.CreateIncoming(clone);
            }

            public void HandshakeAllSessions()
            {
                Session[] clone;
                lock (Sessions)
                {
                    clone = Sessions.ToArray();
                }

                foreach (Session session in clone)
                {
                    session.Handshake(new PrivateKeyGenerator().Generate().PublicKey);
                }
            }

            public void CreateNewIncomingSessions(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    Session template = CreateIncomingSessionTemplate(new PrivateKeyGenerator().Generate().PublicKey);
                    template.RemoteHost = $"1.2.3.{i + 1}";
                    RlpxPeer.CreateIncoming(template);
                }
            }

            public List<NetworkNode> CreateNodes(int count, bool diverseAddresses = false)
            {
                List<NetworkNode> nodes = [];
                for (int i = 0; i < count; i++)
                {
                    PrivateKeyGenerator generator = new();
                    NetworkNode node = diverseAddresses
                        ? new NetworkNode(generator.Generate().PublicKey, $"52.141.{i / 256}.{i % 256}", 30303)
                        : new NetworkNode(GenerateEnode(generator));
                    nodes.Add(node);
                }

                return nodes;
            }

            public void DiscoverNew(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    TestNodeSource.AddNode(new Node(new PrivateKeyGenerator().Generate().PublicKey, "1.2.3.4", 1234));
                }
            }

            public void DisconnectAllSessions()
            {
                Session[] clone;
                lock (Sessions)
                {
                    clone = Sessions.ToArray();
                    Sessions.Clear();
                }

                foreach (Session session in clone)
                {
                    session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
                }
            }

            public async Task WaitForActivePeersAsync(int target, TimeSpan timeout)
            {
                // ActivePeers is updated synchronously by RlpxHostOnSessionCreated (subscribed by
                // PeerManager), which the mock invokes before raising ConnectCalled. So every time
                // ConnectCalled fires we have just gained — or could lose to a pending OnDisconnected —
                // an active peer. Re-evaluate on each ConnectCalled signal until we observe the target.
                if (PeerPool.ActivePeers.Count >= target) return;

                TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Action handler = () =>
                {
                    if (PeerPool.ActivePeers.Count >= target) tcs.TrySetResult();
                };
                RlpxPeer.ConnectCalled += handler;
                try
                {
                    if (PeerPool.ActivePeers.Count >= target) return;
                    await tcs.Task.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    Assert.Fail($"Timed out after {timeout} waiting for {target} active peers (current: {PeerPool.ActivePeers.Count})");
                }
                finally
                {
                    RlpxPeer.ConnectCalled -= handler;
                }
            }

            public string GenerateEnode(PrivateKeyGenerator generator = null)
            {
                generator ??= new PrivateKeyGenerator();
                string enode = $"enode://{generator.Generate().PublicKey.ToString(false)}@52.141.78.53:30303";
                return enode;
            }

            public async ValueTask DisposeAsync()
            {
                await PeerManager.StopAsync();
                // Its timers would otherwise keep queueing work for the rest of the run.
                await PeerPool.StopAsync();
                _addressPolicy.Dispose();
            }
        }

        internal class RlpxMock(List<Session> sessions) : IRlpxHost
        {
            public bool AutoAuthenticateIncoming { get; set; } = true;
            public Action<Session, int?> AuthenticateIncoming { get; set; } = (_, _) => { };
            public Action<Session, PublicKey> CompleteInboundHandshake { get; set; } = (session, key) => session.Handshake(key);
            private readonly List<Session> _sessions = sessions;
            private NodeFilter _nodeFilter = NodeFilter.AcceptAll;
            private int _connectAsyncCallsCount;
            private int _shouldContactCallsCount;
            private int _endpointEligibilityChecks;
            private int _plainOperationCanceledCount;
            private bool _throwPlainOperationCanceledOnCancellation;

            public ISessionMonitor SessionMonitor { get; }

            public event Action? ConnectCalled;

            public Task Init() => Task.CompletedTask;

            public Task<bool> ConnectAsync(Node node, CancellationToken cancellationToken = default)
            {
                if (_throwPlainOperationCanceledOnCancellation)
                {
                    Task<bool> connectTask = ThrowPlainOperationCanceled(cancellationToken);
                    OnConnectCalled();
                    return connectTask;
                }

                if (_isFailing)
                {
                    OnConnectCalled();
                    return Task.FromResult(false);
                }

                Session session = new(30313, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                    LimboLogs.Instance);
                Track(session);
                lock (_sessions)
                {
                    _sessions.Add(session);
                }

                SessionCreated?.Invoke(this, new SessionEventArgs(session));
                OnConnectCalled();
                return Task.FromResult(true);
            }

            // Counted only once the call's session is registered and announced, so a waiter released by the
            // count never observes a connect whose session it cannot see yet.
            private void OnConnectCalled()
            {
                Interlocked.Increment(ref _connectAsyncCallsCount);
                ConnectCalled?.Invoke();
            }

            public async Task WaitForConnectCallsAsync(int totalCount, TimeSpan timeout)
            {
                if (ConnectAsyncCallsCount >= totalCount) return;

                TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Action handler = () =>
                {
                    if (ConnectAsyncCallsCount >= totalCount) tcs.TrySetResult();
                };
                ConnectCalled += handler;
                try
                {
                    if (ConnectAsyncCallsCount >= totalCount) return;
                    await tcs.Task.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    Assert.Fail($"Timed out after {timeout} waiting for {totalCount} ConnectAsync calls (current: {ConnectAsyncCallsCount})");
                }
                finally
                {
                    ConnectCalled -= handler;
                }
            }

            public void CreateRandomIncoming(int port = 12345)
            {
                Session session = new(30313, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
                Track(session);
                lock (_sessions)
                {
                    _sessions.Add(session);
                }

                session.RemoteHost = "1.2.3.4";
                session.RemotePort = 12345;
                SessionCreated?.Invoke(this, new SessionEventArgs(session));
                session.Handshake(new PrivateKeyGenerator().Generate().PublicKey);
                if (AutoAuthenticateIncoming) AuthenticateIncoming(session, port);
            }

            public int ConnectAsyncCallsCount => Volatile.Read(ref _connectAsyncCallsCount);

            public int ShouldContactCallsCount => Volatile.Read(ref _shouldContactCallsCount);

            public int EndpointEligibilityChecks => Volatile.Read(ref _endpointEligibilityChecks);

            public bool IsEndpointRejected(Node node)
            {
                Interlocked.Increment(ref _endpointEligibilityChecks);
                return false;
            }

            public int PlainOperationCanceledCount => Volatile.Read(ref _plainOperationCanceledCount);

            public void ThrowPlainOperationCanceledOnCancellation() => _throwPlainOperationCanceledOnCancellation = true;

            public Task Shutdown() => Task.CompletedTask;

            public int LocalPort => 0;
            public event EventHandler<SessionEventArgs> SessionCreated;
            public event SessionDisconnectedEventHandler SessionDisconnected;

            public void CreateIncoming(params Session[] sessions) => CreateIncoming(AutoAuthenticateIncoming, sessions);

            /// <summary>Handshakes an incoming session for each template, with the template's claimed remote key.</summary>
            /// <param name="receiveHello">When false, the remote never proves that it holds the claimed key.</param>
            public void CreateIncoming(bool receiveHello, params Session[] sessions)
            {
                List<Session> incomingSessions = [];
                foreach (Session session in sessions)
                {
                    Session sessionIn = new(30313, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
                    sessionIn.RemoteHost = session.RemoteHost;
                    sessionIn.RemotePort = session.RemotePort;
                    Track(sessionIn);
                    SessionCreated?.Invoke(this, new SessionEventArgs(sessionIn));
                    CompleteInboundHandshake(sessionIn, session.RemoteNodeId);
                    if (receiveHello) AuthenticateIncoming(sessionIn, null);
                    incomingSessions.Add(sessionIn);
                }

                lock (_sessions)
                {
                    _sessions.AddRange(incomingSessions);
                }
            }

            public void CreateIncomingBeforeHandshake(Session session)
            {
                Track(session);
                SessionCreated?.Invoke(this, new SessionEventArgs(session));
            }

            private bool _isFailing;

            public void MakeItFail(bool fail = true) => _isFailing = fail;

            /// <summary>Addresses accepted for contact, that is, the ones that spent a filter entry.</summary>
            public ConcurrentBag<IPAddress> ContactedIps { get; } = [];

            /// <summary>Replaces the accept-everything default with the production recent-IP filter.</summary>
            public void UseRecentIpFilter() => _nodeFilter = NodeFilter.CreateExact(size: 256, timeout: TimeSpan.FromMinutes(5));

            public bool ShouldContact(IPAddress ip, bool exactOnly = false)
            {
                Interlocked.Increment(ref _shouldContactCallsCount);
                if (!_nodeFilter.TryAccept(ip, exactOnly)) return false;

                ContactedIps.Add(ip);
                return true;
            }

            private void Track(Session session) => session.Disconnected += OnSessionDisconnected;

            private async Task<bool> ThrowPlainOperationCanceled(CancellationToken cancellationToken)
            {
                TaskCompletionSource cancellationRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    cancellationRequested);
                await cancellationRequested.Task;
                Interlocked.Increment(ref _plainOperationCanceledCount);
                throw new OperationCanceledException(cancellationToken);
            }

            private void OnSessionDisconnected(object? sender, DisconnectEventArgs args)
            {
                ISession session = (ISession)sender!;
                session.Disconnected -= OnSessionDisconnected;
                SessionDisconnected?.Invoke(this, session, args);
            }
        }

        private class InMemoryStorage : INetworkStorage
        {
            private readonly ConcurrentDictionary<PublicKey, NetworkNode> _nodes =
                new();

            public NetworkNode[] GetPersistedNodes() => _nodes.Select(static kvp => kvp.Value).ToArray();

            public void UpdateNode(NetworkNode node)
            {
                _nodes[node.NodeId] = node;
                _pendingChanges = true;
            }

            public void UpdateNodes(IEnumerable<NetworkNode> nodes)
            {
                foreach (NetworkNode node in nodes)
                {
                    UpdateNode(node);
                }
            }

            public void RemoveNode(PublicKey nodeId) => _pendingChanges = true;

            public void StartBatch()
            {
            }

            public void Commit()
            {
            }

            private bool _pendingChanges;

            public int PersistedNodesCount => _nodes.Count;

            public bool AnyPendingChange() => _pendingChanges;
        }

        [Test]
        public async Task Static_peer_can_restart_while_outbound_redial_is_paced()
        {
            await using Context ctx = new();
            Node node = new(TestItem.PublicKeyB, "1.2.3.4", 30303, isStatic: true);
            ctx.PeerPool.GetOrAdd(node);
            ctx.PeerManager.Start();
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(node.Id));
            Session previous = ctx.Sessions.Single(session => session.Direction == ConnectionDirection.In);
            Assert.That(ctx.PeerManager.ActivePeers.Single().InSession, Is.SameAs(previous));
            previous.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "peer restart");
            Assert.That(ctx.Stats.GetOrAdd(node).IsConnectionDelayed(DateTime.UtcNow).Result, Is.True);

            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(node.Id));
            Session restarted = ctx.Sessions.Last(session => session.Direction == ConnectionDirection.In);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(restarted.IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers.Single().InSession, Is.SameAs(restarted));
            }
        }

        [Test]
        public async Task Failed_outbound_dial_does_not_block_peer_initiated_recovery([Values] bool isStatic)
        {
            await using Context ctx = new(parallelism: 1);
            Node node = new(TestItem.PublicKeyB, "1.2.3.4", 30303, isStatic: isStatic);
            Task failedDialRemoved = ObserveFailedDialRemoval(ctx, node);
            ctx.PeerPool.GetOrAdd(node);
            ctx.RlpxPeer.MakeItFail();
            ctx.PeerManager.Start();
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(5));
            await failedDialRemoved.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(ctx.Stats.GetOrAdd(node).DidEventHappen(NodeStatsEventType.ConnectionFailed), Is.True);
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(node.Id));
            Session incoming = ctx.Sessions.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers.Single().InSession, Is.SameAs(incoming));
            }
        }

        [Test]
        public async Task Authenticated_inbound_distinguishes_bans_from_dial_pacing(
            [Range(0, 9)] int restriction, [Values(0, 1, 2)] int privilege)
        {
            await using Context ctx = new();
            Node node = new(TestItem.PublicKeyB, "1.2.3.4", 30303)
            {
                IsStatic = privilege == 1,
                IsTrusted = privilege == 2
            };
            if (privilege != 0) ctx.PeerPool.GetOrAdd(node);
            switch (restriction)
            {
                case 0: ctx.Stats.ReportDisconnect(node, DisconnectType.Local, DisconnectReason.BreachOfProtocol); break;
                case 1: ctx.Stats.ReportFailedValidation(node, CompatibilityValidationType.NetworkId); break;
                case 2: ctx.Stats.ReportEvent(node, NodeStatsEventType.ConnectionFailed); break;
                case 3: ctx.Stats.ReportEvent(node, NodeStatsEventType.ConnectionFailedTargetUnreachable); break;
                case 4: ctx.Stats.ReportEvent(node, NodeStatsEventType.Connecting); break;
                case 5: ctx.Stats.ReportDisconnect(node, DisconnectType.Remote, DisconnectReason.TooManyPeers); break;
                case 6: ctx.Stats.ReportDisconnect(node, DisconnectType.Remote, DisconnectReason.ClientQuitting); break;
                case 7: ctx.Stats.ReportDisconnect(node, DisconnectType.Local, DisconnectReason.TooManyPeers); break;
                case 8: ctx.Stats.ReportDisconnect(node, DisconnectType.Local, DisconnectReason.ReceiveMessageTimeout); break;
                case 9: ctx.Stats.ReportDisconnect(node, DisconnectType.Local, DisconnectReason.UselessPeer); break;
            }
            ctx.RlpxPeer.MakeItFail();
            ctx.PeerManager.Start();
            Session template = CreateIncomingSessionTemplate(node.Id);
            template.RemoteHost = "5.6.7.8";
            ctx.RlpxPeer.CreateIncoming(template);
            Session incoming = ctx.Sessions.Single();
            bool rejected = restriction == 1 || (restriction == 0 && privilege == 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.EqualTo(rejected));
                Assert.That(ctx.PeerManager.ActivePeers.Any(peer => peer.InSession == incoming), Is.EqualTo(!rejected));
            }
            if (restriction == 0 && privilege == 0)
            {
                ctx.RlpxPeer.CreateIncoming(template);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ctx.Sessions.Last().IsClosing, Is.True);
                    Assert.That(ctx.Stats.GetOrAdd(node).IsInboundBanned(DateTime.UtcNow), Is.True,
                        "an unregistered rejected session cannot replace the original misconduct with its local refusal reason");
                }
            }
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyC));
            Assert.That(ctx.PeerManager.ActivePeers.Any(peer => peer.Node.Id == TestItem.PublicKeyC), Is.True);
        }

        [Test]
        public async Task Inbound_candidate_ip_quota_recovers_after_removal([Values] bool mapped)
        {
            await using Context ctx = new(maxActivePeers: 60);
            ctx.PeerManager.Start();
            for (int i = 0; i < 16; i++) ctx.RlpxPeer.CreateRandomIncoming();
            Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(16));

            Session template = CreateIncomingSessionTemplate(TestItem.PublicKeyB);
            template.RemoteHost = mapped ? "::ffff:1.2.3.4" : "1.2.3.4";
            ctx.RlpxPeer.CreateIncoming(template);
            Session unpooled = ctx.Sessions.Last();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(unpooled.IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(17));
                Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(16));
            }
            unpooled.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "restart");
            Assert.That(ctx.PeerPool.TryGet(unpooled.Node.Id, out _), Is.False);

            ctx.PeerPool.TryRemove(ctx.PeerManager.CandidatePeers.First().Node.Id, out _);
            ctx.RlpxPeer.CreateIncoming(template);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Last().IsClosing, Is.False);
                Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(16));
            }

            Session independent = CreateIncomingSessionTemplate(TestItem.PublicKeyC);
            independent.RemoteHost = "5.6.7.8";
            ctx.RlpxPeer.CreateIncoming(independent);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Last().IsClosing, Is.False);
                Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(17));
            }
        }

        [Test]
        public async Task Discovered_candidates_do_not_consume_inbound_ip_quota()
        {
            await using Context ctx = new(maxActivePeers: 60);
            for (int i = 0; i < 20; i++)
                ctx.PeerPool.GetOrAdd(new Node(new PrivateKeyGenerator().Generate().PublicKey, "1.2.3.4", 30303));
            ctx.PeerManager.Start();
            ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(TestItem.PublicKeyB));
            Session incoming = ctx.Sessions.Last();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(incoming.IsClosing, Is.False);
                Assert.That(ctx.PeerPool.TryGet(incoming.Node.Id, out _), Is.True);
            }
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public async Task Full_inbound_reserve_resolves_existing_cross_dials_before_refusing_new_peers(bool existing, bool keepInbound)
        {
            await using Context ctx = new(maxActivePeers: 3);
            ctx.RlpxPeer.CreateRandomIncoming();
            ctx.RlpxPeer.CreateRandomIncoming();
            Node node = new(keepInbound ? TestItem.PublicKeyB : TestItem.PublicKeyC, "8.8.8.8", 30303);
            Session outgoing = new(30303, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            outgoing.Handshake(node.Id);
            outgoing.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            if (existing)
            {
                Peer peer = ctx.PeerPool.GetOrAdd(node);
                peer.OutSession = outgoing;
                ctx.PeerPool.ActivePeers.TryAdd(node.Id, peer);
            }
            DisconnectReason? incomingReason = null;
            ctx.RlpxPeer.SessionCreated += (_, args) => args.Session.Disconnected += (_, disconnect) => incomingReason = disconnect.DisconnectReason;
            try
            {
                ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(node.Id));
                Session incoming = ctx.Sessions.Last();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(incoming.IsClosing, Is.EqualTo(!existing || !keepInbound));
                    Assert.That(outgoing.IsClosing, Is.EqualTo(existing && keepInbound));
                    Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(existing ? 3 : 2));
                    Assert.That(incomingReason, Is.EqualTo(!existing ? DisconnectReason.TooManyPeers :
                        keepInbound ? (DisconnectReason?)null : DisconnectReason.ReplacingSessionWithOppositeDirection));
                    if (existing)
                    {
                        Peer retained = ctx.PeerManager.ActivePeers.Single(peer => peer.Node.Id == node.Id);
                        Assert.That(keepInbound ? retained.InSession : retained.OutSession, Is.SameAs(keepInbound ? incoming : outgoing));
                    }
                }
            }
            finally
            {
                outgoing.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Local, "test cleanup");
                outgoing.Dispose();
            }
        }

        [Test]
        public async Task Full_pool_at_the_normal_inbound_limit_skips_candidate_and_endpoint_scans(
            [Values(0, 33)] int ordinaryInbound, [Values] bool crossDial)
            => await CheckFullPoolSelection(ordinaryInbound, crossDial);

        [Test]
        public async Task Full_pool_above_the_normal_inbound_limit_scans_candidates([Values] bool crossDial)
            => await CheckFullPoolSelection(34, crossDial);

        private static async Task CheckFullPoolSelection(int ordinaryInbound, bool crossDial)
        {
            bool excessInbound = ordinaryInbound > 33;
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: excessInbound ? clock : TimeProvider.System);
            await ctx.PeerManager.StopAsync();
            IPeerPool pool = CreateForwardingPool(ctx);
            ReplacePeerManager(ctx, pool, excessInbound ? clock : TimeProvider.System);
            if (excessInbound)
            {
                SeedEmptyCandidateWindow(ctx);
                clock.Advance(TimeSpan.FromMinutes(5));
            }
            for (int index = 0; index < ordinaryInbound; index++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            CreatePrivilegedInbound(ctx);
            foreach (NetworkNode node in ctx.CreateNodes(50 - ctx.PeerPool.ActivePeerCount))
            {
                Peer peer = ctx.PeerPool.GetOrAdd(node);
                peer.OutSession = Substitute.For<ISession>();
                ctx.PeerPool.ActivePeers.TryAdd(node.NodeId, peer);
            }
            if (crossDial)
            {
                Peer peer = ordinaryInbound == 0
                    ? ctx.PeerPool.ActivePeers.First(candidate => !candidate.Value.Node.IsStatic && !candidate.Value.Node.IsTrusted).Value
                    : ctx.PeerPool.ActivePeers[ctx.Sessions[ordinaryInbound - 1].Node.Id];
                if (peer.InSession is not null) peer.OutSession = Substitute.For<ISession>();
                else peer.InSession = Substitute.For<ISession>();
            }
            Assert.That(ctx.PeerPool.ActivePeers.Count(candidate => !candidate.Value.Node.IsStatic && !candidate.Value.Node.IsTrusted && candidate.Value.InSession is not null), Is.EqualTo(ordinaryInbound + (crossDial && ordinaryInbound == 0 ? 1 : 0)));
            foreach (NetworkNode node in ctx.CreateNodes(1024)) ctx.PeerPool.GetOrAdd(node);
            int candidateReads = 0;
            pool.Peers.Returns(_ =>
            {
                Interlocked.Increment(ref candidateReads);
                return ctx.PeerPool.Peers;
            });
            int endpointChecksBefore = ctx.RlpxPeer.EndpointEligibilityChecks;
            SelectCandidates(ctx);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(50));
                Assert.That(candidateReads, Is.EqualTo(excessInbound ? 1 : 0));
                if (!excessInbound) Assert.That(ctx.RlpxPeer.EndpointEligibilityChecks, Is.EqualTo(endpointChecksBefore));
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero);
                Assert.That(ctx.Sessions.Take(ordinaryInbound).Count(session => session.IsClosing), Is.Zero);
            }
        }

        [Test]
        public async Task Queued_candidates_follow_policy_changes_without_repeating_selection([Values] bool becomesPublic)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(parallelism: 1, maxActivePeers: 50, timeProvider: clock);
            await ctx.PeerManager.StopAsync();
            IPeerPool pool = CreateForwardingPool(ctx);
            foreach (NetworkNode node in ctx.CreateNodes(49))
            {
                Peer peer = ctx.PeerPool.GetOrAdd(node);
                peer.OutSession = Substitute.For<ISession>();
                ctx.PeerPool.ActivePeers.TryAdd(node.NodeId, peer);
            }
            Node occupyingNode = new(TestItem.PublicKeyE, "8.8.4.4", 30303);
            Peer occupyingPeer = ctx.PeerPool.GetOrAdd(occupyingNode);
            foreach (NetworkNode node in ctx.CreateNodes(1024))
                ctx.PeerPool.GetOrAdd(new Node(node.NodeId, "192.168.2.9", 30303));
            int candidateReads = 0;
            IIPResolver resolver = Substitute.For<IIPResolver>();
            IPAddress external = IPAddress.Parse("192.168.1.8");
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Loopback, external)));
            NetworkConfig config = new() { LocalIp = "127.0.0.1", P2PPort = 0, FilterPeersByRecentIp = false };
            await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver);
            RlpxHost transport = container.Resolve<RlpxHost>();
            IRlpxHost host = Substitute.For<IRlpxHost>();
            host.IsEndpointRejected(Arg.Any<Node>()).Returns(call => transport.IsEndpointRejected(call.Arg<Node>()));
            host.ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>()).Returns(call =>
                transport.ShouldContact(call.ArgAt<IPAddress>(0), call.ArgAt<bool>(1)));
            TaskCompletionSource<Node>[] started = Enumerable.Range(0, 3)
                .Select(_ => new TaskCompletionSource<Node>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            TaskCompletionSource<bool>[] completed = Enumerable.Range(0, 3)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            int attempts = 0;
            host.ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                int index = Interlocked.Increment(ref attempts) - 1;
                if (becomesPublic && index > 0) return Task.FromResult(false);
                started[index].TrySetResult(call.ArgAt<Node>(0));
                return completed[index].Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            });
            TaskCompletionSource waitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource nextLoop = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int fullSlotTimestamps = 0;
            ObservingTimeProvider observedClock = new(clock, () =>
            {
                if (started[0].Task.IsCompleted && ctx.PeerPool.ActivePeers.Count == 50
                    && Interlocked.Increment(ref fullSlotTimestamps) == 3)
                    waitEntered.TrySetResult();
            });
            pool.Peers.Returns(_ =>
            {
                if (Interlocked.Increment(ref candidateReads) == 2)
                    pool.PeerAdded += Raise.Event<EventHandler<PeerEventArgs>>(pool, new PeerEventArgs(occupyingPeer));
                return ctx.PeerPool.Peers;
            });
            pool.PeerCount.Returns(_ =>
            {
                if (started[0].Task.IsCompleted && ctx.PeerPool.ActivePeers.Count == 50)
                    nextLoop.TrySetResult();
                return ctx.PeerPool.PeerCount;
            });
            ctx.PeerManager = new PeerManager(host, pool, ctx.Stats, ctx.NetworkConfig,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance, observedClock);

            ctx.PeerManager.Start();
            // Selection and reserve checks precede the slot-wait timestamp.
            await Task.WhenAny(waitEntered.Task, nextLoop.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Node firstCandidate = await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (becomesPublic)
            {
                external = IPAddress.Parse("8.8.8.8");
                resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                    new IIPResolver.NethermindIp(IPAddress.Loopback, external));
            }
            completed[0].SetResult(false);
            if (becomesPublic) await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
            else
            {
                for (int index = 1; index < started.Length; index++)
                {
                    await started[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (index < started.Length - 1) completed[index].SetResult(false);
                }
            }
            int failedDials = becomesPublic ? 1 : 2;
            // Failed-dial removal also reads the pool to check stats ownership.
            int selectionReads = Volatile.Read(ref candidateReads) - 1 - failedDials;
            await ctx.PeerManager.StopAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(waitEntered.Task.IsCompleted, Is.True);
                Assert.That(selectionReads, Is.EqualTo(becomesPublic ? 2 : 1));
                Assert.That(attempts, Is.EqualTo(becomesPublic ? 1 : 3));
                for (int index = 0; index < failedDials; index++)
                    Assert.That(ctx.PeerPool.Peers[started[index].Task.Result.Id].Stats.DidEventHappen(NodeStatsEventType.ConnectionFailed), Is.True);
                if (becomesPublic)
                {
                    host.Received(1).ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>());
                    Assert.That(ctx.PeerPool.Peers.Select(pair => pair.Value).Where(peer => peer.Node.Host == "192.168.2.9" && !peer.Node.Id.Equals(firstCandidate.Id))
                        .All(peer => peer.Stats.CurrentNodeReputation(DateTime.UtcNow) == 0), Is.True);
                }
                else Assert.That(started.Select(attempt => attempt.Task.Result.Id).Distinct().Count(), Is.EqualTo(3));
            }
        }

        [Test]
        public async Task Rate_limit_resume_rechecks_endpoint_policy_before_contact([Values] bool becomesPublic)
        {
            await using Context ctx = new(parallelism: 1, maxActivePeers: 1);
            IPeerPool pool = CreateForwardingPool(ctx);
            ctx.NetworkConfig.MaxOutgoingConnectPerSec = 1;
            Node candidate = new(TestItem.PublicKeyD, "192.168.1.9", 30303);
            ctx.PeerPool.GetOrAdd(candidate);
            IIPResolver resolver = Substitute.For<IIPResolver>();
            IPAddress external = IPAddress.Parse("192.168.1.8");
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Loopback, external)));
            NetworkConfig config = new()
            {
                LocalIp = "127.0.0.1",
                P2PPort = 0,
                ProcessingThreadCount = 1,
                FilterPeersByRecentIp = false,
                RlpxHostShutdownCloseTimeoutMs = 100
            };
            RlpxHostIntegrationTests.FailingClientChannelFactory channels = new();
            await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver, channelFactory: channels);
            RlpxHost transport = container.Resolve<RlpxHost>();
            IRlpxHost host = Substitute.For<IRlpxHost>();
            TaskCompletionSource claimReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int decisionObserved = 0;
            FieldInfo pendingClaims = typeof(PeerManager).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            RateLimiter? limiter = null;
            host.IsEndpointRejected(Arg.Any<Node>()).Returns(call =>
            {
                if ((int)pendingClaims.GetValue(ctx.PeerManager)! == 0)
                {
                    // RateLimiter uses Stopwatch and Task.Delay, so this check needs its real one-second debt.
                    limiter!.TryReserve(TimeSpan.Zero, out _);
                }
                else
                {
                    if (becomesPublic)
                    {
                        external = IPAddress.Parse("8.8.8.8");
                        resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                            new IIPResolver.NethermindIp(IPAddress.Loopback, external));
                    }
                    Volatile.Write(ref decisionObserved, 1);
                }
                return transport.IsEndpointRejected(call.Arg<Node>());
            });
            host.ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>()).Returns(call =>
                transport.ShouldContact(call.ArgAt<IPAddress>(0), call.ArgAt<bool>(1)));
            host.ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(call =>
                ConnectCandidate(call.ArgAt<Node>(0), call.ArgAt<CancellationToken>(1)));
            pool.PeerCount.Returns(_ =>
            {
                if (Volatile.Read(ref decisionObserved) != 0 && (int)pendingClaims.GetValue(ctx.PeerManager)! == 0)
                    claimReleased.TrySetResult();
                return ctx.PeerPool.PeerCount;
            });
            ctx.PeerManager = new PeerManager(host, pool, ctx.Stats, ctx.NetworkConfig,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance);
            limiter = (RateLimiter)typeof(PeerManager)
                .GetField("_outgoingConnectionRateLimiter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.PeerManager)!;
            try
            {
                await transport.Init();
                ctx.PeerManager.Start();
                await claimReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ctx.PeerManager.StopAsync();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(channels.CreatedClientChannels, Is.EqualTo(becomesPublic ? 0 : 1));
                    Assert.That(ctx.PeerPool.Peers[candidate.Id].Stats.DidEventHappen(NodeStatsEventType.Connecting), Is.EqualTo(!becomesPublic));
                    Assert.That(ctx.PeerPool.Peers[candidate.Id].Stats.DidEventHappen(NodeStatsEventType.ConnectionFailed), Is.EqualTo(!becomesPublic));
                    if (becomesPublic)
                    {
                        host.DidNotReceive().ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>());
                        _ = host.DidNotReceive().ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>());
                        Assert.That(ctx.Stats.GetCurrentReputation(candidate), Is.Zero);
                    }
                    else host.Received(1).ShouldContact(candidate.Address.Address, Arg.Any<bool>());
                }
            }
            finally
            {
                await ctx.PeerManager.StopAsync();
                await transport.Shutdown();
            }

            async Task<bool> ConnectCandidate(Node node, CancellationToken token)
            {
                if (becomesPublic)
                {
                    external = IPAddress.Parse("8.8.8.8");
                    resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                        new IIPResolver.NethermindIp(IPAddress.Loopback, external));
                }
                Volatile.Write(ref decisionObserved, 1);
                return await transport.ConnectAsync(node, token);
            }
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public async Task Cheap_candidate_filters_skip_endpoint_validation(bool zeroPort, bool addedAfterStart)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            Node candidate = new(TestItem.PublicKeyD, "8.8.4.4", zeroPort ? 0 : 30303);
            if (!zeroPort) ctx.Stats.ReportEvent(candidate, NodeStatsEventType.ConnectionFailed);
            if (!addedAfterStart) ctx.PeerPool.GetOrAdd(candidate);

            ctx.PeerManager.Start();
            await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
            if (addedAfterStart) ctx.PeerPool.GetOrAdd(candidate);
            await ctx.PeerManager.StopAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.EndpointEligibilityChecks, Is.Zero);
                Assert.That(ctx.RlpxPeer.ShouldContactCallsCount, Is.Zero);
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero);
            }
        }

        [Test]
        public async Task Incoming_admission_reads_the_pool_once()
        {
            await using Context ctx = new(maxActivePeers: 50);
            foreach (NetworkNode candidate in ctx.CreateNodes(1024)) ctx.PeerPool.GetOrAdd(candidate);
            int poolReads = 0;
            IPeerPool pool = CreateForwardingPool(ctx);
            pool.Peers.Returns(_ =>
            {
                poolReads++;
                return ctx.PeerPool.Peers;
            });
            ReplacePeerManager(ctx, pool);
            poolReads = 0;

            ctx.RlpxPeer.CreateRandomIncoming();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Single().IsClosing, Is.False);
                Assert.That(poolReads, Is.EqualTo(1), "only the final pool ownership lookup is needed during admission");
            }
        }

        [Test]
        public async Task Candidate_ip_capacity_is_reused_after_removal([Values] bool seededBeforeStart)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            if (!seededBeforeStart) ctx.PeerManager.Start();
            for (int i = 0; i < 16; i++) ctx.RlpxPeer.CreateRandomIncoming();
            ctx.RlpxPeer.CreateRandomIncoming();
            Session withoutCandidate = ctx.Sessions.Last();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(withoutCandidate.IsClosing, Is.False);
                Assert.That(ctx.PeerPool.Peers.ContainsKey(withoutCandidate.Node.Id), Is.False);
                Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(16));
            }

            if (seededBeforeStart) ctx.PeerManager.Start();
            Session removed = ctx.Sessions.First();
            removed.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");
            ctx.PeerPool.TryRemove(removed.Node.Id, out _);
            ctx.RlpxPeer.CreateRandomIncoming();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Last().IsClosing, Is.False);
                Assert.That(ctx.PeerPool.Peers.ContainsKey(ctx.Sessions.Last().Node.Id), Is.True);
                Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(16));
            }
            ctx.RlpxPeer.CreateRandomIncoming();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Last().IsClosing, Is.False);
                Assert.That(ctx.PeerPool.Peers.ContainsKey(ctx.Sessions.Last().Node.Id), Is.False);
                Assert.That(ctx.PeerManager.CandidatePeers, Has.Count.EqualTo(16));
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Candidate_without_stats_does_not_prevent_an_empty_selection_pass(bool isStatic, bool onlyStaticPool)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            Node candidate = new(TestItem.PublicKeyB, "8.8.8.8", 30303) { IsStatic = isStatic };
            Peer peer = new(candidate);
            if (onlyStaticPool)
            {
                IPeerPool pool = CreateForwardingPool(ctx);
                ReplacePeerManager(ctx, pool, clock);
                SeedEmptyCandidateWindow(ctx);
                pool.StaticPeers.Returns([peer]);
            }
            else
            {
                SeedEmptyCandidateWindow(ctx);
                ctx.PeerPool.Peers.TryAdd(candidate.Id, peer);
            }

            SelectCandidates(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(48));
                Assert.That(ctx.Sessions.All(session => !session.IsClosing), Is.True);
            }
        }

        [Test]
        public async Task Static_only_custom_pool_candidates_are_ranked_and_dialed()
        {
            await using Context ctx = new(maxActivePeers: 50);
            ctx.NetworkConfig.NumConcurrentOutgoingConnects = 1;
            IPeerPool pool = CreateForwardingPool(ctx);
            Node lower = new(TestItem.PublicKeyD, "8.8.8.8", 30303) { IsStatic = true };
            Node higher = new(TestItem.PublicKeyE, "8.8.4.4", 30303) { IsStatic = true };
            Peer lowerPeer = new(lower, ctx.Stats.GetOrAdd(lower));
            Peer higherPeer = new(higher, ctx.Stats.GetOrAdd(higher));
            ctx.Stats.ReportEvent(higher, NodeStatsEventType.DiscoveryNeighboursIn);
            pool.StaticPeers.Returns([lowerPeer, higherPeer]);
            ReplacePeerManager(ctx, pool);

            ctx.PeerManager.Start();
            await ctx.RlpxPeer.WaitForConnectCallsAsync(2, TimeSpan.FromSeconds(5));

            Assert.That(ctx.Sessions.Where(session => session.Direction == ConnectionDirection.Out).Select(session => session.Node.Id),
                Is.EqualTo(new[] { higher.Id, lower.Id }));
        }

        [Test]
        public async Task Custom_pool_peer_without_stats_can_be_added_and_admitted([Values(ConnectionDirection.In, ConnectionDirection.Out)] ConnectionDirection direction)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            IPeerPool pool = CreateForwardingPool(ctx);
            ReplacePeerManager(ctx, pool, clock);
            Node node = new(TestItem.PublicKeyD, "1.2.3.4", 30303);
            Peer peer = new(node);
            ctx.PeerPool.Peers.TryAdd(node.Id, peer);
            ctx.PeerManager.Start();

            pool.PeerAdded += Raise.Event<EventHandler<PeerEventArgs>>(pool, new PeerEventArgs(peer));
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero);
            if (direction == ConnectionDirection.In) ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(node.Id));
            else
            {
                ctx.PeerPool.ActivePeers.TryAdd(node.Id, peer);
                await ctx.RlpxPeer.ConnectAsync(node);
                ctx.Sessions.Single().Handshake(node.Id);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Single().IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers.Single(), Is.SameAs(peer));
            }
        }

        [Test]
        public async Task Inbound_before_start_releases_admission_order_and_subscription([Values] bool stop)
        {
            await using Context ctx = new(maxActivePeers: 50);
            ctx.RlpxPeer.CreateRandomIncoming();
            Session session = ctx.Sessions.Single();
            if (stop) await ctx.PeerManager.StopAsync();
            else session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "closed");

            IDictionary admissions = (IDictionary)typeof(PeerManager)
                .GetField("_inboundAdmissionOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.PeerManager)!;
            object handlers = typeof(Session).GetField("_disconnectedHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            Delegate?[] listeners = (Delegate?[])handlers.GetType()
                .GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handlers)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(admissions.Count, Is.Zero);
                Assert.That(listeners.Any(listener => ReferenceEquals(listener?.Target, ctx.PeerManager)), Is.False);
            }
        }

        [TestCase(299, false, 33)]
        [TestCase(300, false, 48)]
        [TestCase(300, true, 33)]
        public async Task Inbound_capacity_tracks_the_empty_candidate_window(int seconds, bool candidate, int admitted)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            if (candidate) ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyB, "8.8.8.8", 30303));
            SelectCandidates(ctx);
            clock.Advance(TimeSpan.FromSeconds(seconds));
            for (int i = 0; i < 50; i++) ctx.RlpxPeer.CreateRandomIncoming();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(admitted));
                Assert.That(ctx.Sessions.Count(session => !session.IsClosing), Is.EqualTo(admitted));
                Assert.That(ctx.RlpxPeer.ShouldContactCallsCount, Is.Zero, "admission must not consume dial pacing tokens");
            }
        }

        [Test]
        public async Task A_new_candidate_restores_the_reserve_until_a_fresh_empty_window_elapses()
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 33; i++) ctx.RlpxPeer.CreateRandomIncoming();
            Node candidate = new(TestItem.PublicKeyB, "8.8.8.8", 30303);
            ctx.PeerPool.GetOrAdd(candidate);
            SelectCandidates(ctx);
            ctx.RlpxPeer.CreateRandomIncoming();
            Assert.That(ctx.Sessions.Last().IsClosing, Is.True);
            clock.Advance(TimeSpan.FromMinutes(1));
            ctx.PeerPool.TryRemove(candidate.Id, out _);
            SelectCandidates(ctx);
            clock.Advance(TimeSpan.FromSeconds(299));
            ctx.RlpxPeer.CreateRandomIncoming();
            Assert.That(ctx.Sessions.Last().IsClosing, Is.True);
            clock.Advance(TimeSpan.FromSeconds(1));
            ctx.RlpxPeer.CreateRandomIncoming();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Last().IsClosing, Is.False);
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(34));
            }
        }

        [Test]
        public async Task Undialable_candidates_do_not_hold_the_reserve([Values] CandidateBlock reason, [Values] bool priorCandidate)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            if (priorCandidate) SeedEmptyCandidateWindow(ctx);
            Node candidate = new(TestItem.PublicKeyB, "8.8.8.8", reason == CandidateBlock.ZeroPort ? 0 : 30303)
            {
                IsStatic = reason == CandidateBlock.Delay
            };
            ctx.PeerPool.GetOrAdd(candidate);
            if (reason == CandidateBlock.Compatibility) ctx.Stats.ReportFailedValidation(candidate, CompatibilityValidationType.NetworkId);
            else if (reason == CandidateBlock.Delay) ctx.Stats.ReportEvent(candidate, NodeStatsEventType.ConnectionFailed);
            SelectCandidates(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 49; i++) ctx.RlpxPeer.CreateRandomIncoming();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(priorCandidate ? 48 : 33));
                Assert.That(ctx.Sessions.Last().IsClosing, Is.True);
            }
        }

        public enum CandidateBlock { ZeroPort, Compatibility, Delay }

        [Test]
        public async Task Construction_without_candidate_selection_does_not_relax_the_reserve([Values] bool start)
        {
            ManualTimeProvider clock = new();
            TaskCompletionSource emptySelectionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ObservingTimeProvider observedClock = new(clock, () => { }, dueTime =>
            {
                if (dueTime == TimeSpan.FromSeconds(1)) emptySelectionCompleted.TrySetResult();
            });
            await using Context ctx = new(maxActivePeers: 50, timeProvider: observedClock);
            if (start)
            {
                ctx.PeerManager.Start();
                await emptySelectionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            clock.Advance(TimeSpan.FromMinutes(10));
            for (int i = 0; i < 50; i++) ctx.RlpxPeer.CreateRandomIncoming();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(33));
                Assert.That(ctx.Sessions.Count(session => !session.IsClosing), Is.EqualTo(33));
            }
        }

        [Test]
        public async Task Authenticated_outbound_restores_the_reserve_newest_inbound_first()
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            CreatePrivilegedInbound(ctx);
            Session[] privileged = ctx.Sessions.ToArray();
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            Session[] inbound = ctx.Sessions.Skip(2).ToArray();
            List<NetworkNode> candidates = ctx.CreateNodes(15);
            foreach (NetworkNode candidate in candidates)
            {
                Session outgoing = await ConnectOwnedOutgoing(ctx, new Node(candidate));
                Assert.That(inbound.Count(session => session.IsClosing), Is.EqualTo(candidates.IndexOf(candidate)), "transport success is not accepted Hello");
                ctx.AcceptHello(outgoing);
                ctx.PeerManager.OnP2PProtocolInitialized(outgoing);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(inbound.Take(33).All(session => !session.IsClosing), Is.True);
                Assert.That(inbound.Skip(33).All(session => session.IsClosing), Is.True);
                Assert.That(privileged.All(session => !session.IsClosing), Is.True);
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(15));
                Assert.That(ctx.PeerManager.ActivePeers.Count(peer => !peer.Node.IsStatic && !peer.Node.IsTrusted && peer.InSession is { IsClosing: false }), Is.EqualTo(33));
                Assert.That(ctx.PeerManager.ActivePeers.Where(peer => peer.OutSession is { IsClosing: false }).Select(peer => peer.Node.Id),
                    Is.EquivalentTo(candidates.Select(candidate => candidate.NodeId)));
            }
        }

        [Test]
        public async Task Restoring_the_reserve_does_not_penalize_the_shed_peer()
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 9, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 7; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            Session newest = ctx.Sessions.Last();
            ctx.Stats.ReportEvent(newest.Node, NodeStatsEventType.SyncStarted);
            long reputation = ctx.Stats.GetOrAdd(newest.Node).CurrentNodeReputation(DateTime.UtcNow);
            Session outgoing = await ConnectOwnedOutgoing(ctx, new Node(TestItem.PublicKeyD, "8.8.8.8", 30303));
            ctx.PeerManager.Start();
            ctx.AcceptHello(outgoing);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(newest.IsClosing, Is.True);
                Assert.That(ctx.Stats.GetOrAdd(newest.Node).CurrentNodeReputation(DateTime.UtcNow), Is.EqualTo(reputation));
                Assert.That(ctx.Stats.GetOrAdd(newest.Node).IsInboundBanned(DateTime.UtcNow), Is.False);
            }
        }

        [Test]
        public async Task Closed_channel_outbound_hello_preserves_inbound_until_an_open_replacement([Values] bool channelClosed)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            Session[] inbound = ctx.Sessions.ToArray();
            Session outgoing = await ConnectOwnedOutgoing(ctx, new Node(TestItem.PublicKeyD, "8.8.8.8", 30303));
            if (channelClosed) outgoing.MarkChannelClosed();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(outgoing.State, Is.EqualTo(SessionState.Initialized));
                Assert.That(outgoing.IsClosing, Is.False);
                Assert.That(outgoing.IsChannelClosed, Is.EqualTo(channelClosed));
            }
            ctx.AcceptHello(outgoing);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(inbound.Count(session => session.IsClosing), Is.EqualTo(channelClosed ? 0 : 1));
                Assert.That(inbound.Last().IsClosing, Is.EqualTo(!channelClosed));
                Assert.That(ctx.PeerPool.ActivePeers[outgoing.Node.Id].OutSession, Is.SameAs(outgoing));
            }
        }

        [Test]
        public async Task Unowned_or_closing_outbound_hello_does_not_displace_inbound([Values] bool closing)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            ISession owner = AddOwnedSession(ctx, ConnectionDirection.Out);
            ISession unrelated = CreateSession(owner.Node, ConnectionDirection.Out);
            unrelated.IsClosing.Returns(closing);
            ctx.PeerManager.OnP2PProtocolInitialized(unrelated);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Sessions.Count(session => session.IsClosing), Is.Zero);
                Assert.That(ctx.PeerPool.ActivePeers[owner.Node.Id].OutSession, Is.SameAs(owner));
            }
        }

        [Test]
        public async Task Candidate_selection_and_transport_result_do_not_shed_inbound([Values] bool failure)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            Node candidate = new(TestItem.PublicKeyD, "8.8.8.8", 30303);
            Task failedDialRemoved = ObserveFailedDialRemoval(ctx, candidate, clock);
            ctx.RlpxPeer.UseRecentIpFilter();
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            Session[] inbound = ctx.Sessions.ToArray();
            ctx.RlpxPeer.MakeItFail(failure);
            ctx.PeerPool.GetOrAdd(candidate);
            await (ValueTask<List<Peer>?>)typeof(PeerManager).GetMethod("GetRemainingCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ctx.PeerManager, null)!;
            Assert.That(inbound.Count(session => session.IsClosing), Is.Zero, "one stopped-manager selection does not shed inbound");
            TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.RlpxPeer.ConnectCalled += () => connected.TrySetResult();
            ctx.PeerManager.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (failure)
            {
                await failedDialRemoved.WaitAsync(TimeSpan.FromSeconds(5));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ctx.Stats.GetOrAdd(candidate).DidEventHappen(NodeStatsEventType.ConnectionFailed), Is.True);
                    Assert.That(ctx.PeerPool.ActivePeers.ContainsKey(candidate.Id), Is.False);
                }
            }
            Assert.That(inbound.Count(session => session.IsClosing), Is.Zero);
            if (!failure)
            {
                Session outgoing = ctx.Sessions.Single(session => session.Direction == ConnectionDirection.Out);
                outgoing.Handshake(outgoing.Node.Id);
                InitSession(outgoing);
                Assert.That(inbound.Count(session => session.IsClosing), Is.Zero, "AUTH is not accepted Hello");
                ctx.AcceptHello(outgoing);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(inbound.Count(session => session.IsClosing), Is.EqualTo(1));
                    Assert.That(inbound.Last().IsClosing, Is.True);
                }
            }
            await ctx.PeerManager.StopAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(1));
                Assert.That(inbound.Count(session => session.IsClosing), Is.EqualTo(failure ? 0 : 1));
            }
        }

        [Test]
        public async Task accepted_residual_full_active_pool_waits_for_capacity_before_replacement()
        {
            // Fifty active entries consume every ordinary dial slot, even with excess inbound.
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            IPeerPool pool = CreateForwardingPool(ctx);
            ReplacePeerManager(ctx, pool, clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            CreatePrivilegedInbound(ctx);
            ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyD, "8.8.8.8", 30303));
            TaskCompletionSource iterationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int poolReads = 0;
            int selectionObserved = 0;
            pool.Peers.Returns(_ =>
            {
                if (Interlocked.Increment(ref poolReads) == 2) Volatile.Write(ref selectionObserved, 1);
                return ctx.PeerPool.Peers;
            });
            pool.PeerCount.Returns(_ =>
            {
                if (Volatile.Read(ref selectionObserved) != 0) iterationCompleted.TrySetResult();
                return ctx.PeerPool.PeerCount;
            });
            ctx.PeerManager.Start();
            await iterationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero);
                Assert.That(ctx.Sessions.Count(session => session.IsClosing), Is.Zero);
            }
            ctx.Sessions.First().MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "capacity released");
            await ctx.RlpxPeer.WaitForConnectCallsAsync(1, TimeSpan.FromSeconds(5));
            Session outgoing = ctx.Sessions.Single(session => session.Direction == ConnectionDirection.Out);
            outgoing.Handshake(outgoing.Node.Id);
            InitSession(outgoing);
            ctx.AcceptHello(outgoing);
            await ctx.PeerManager.StopAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(1));
                Assert.That(ctx.Sessions.Take(48).Count(session => session.IsClosing), Is.EqualTo(2));
                Assert.That(outgoing.IsClosing, Is.False);
            }
        }

        [Test]
        public async Task Pending_newest_inbound_displacement_is_claimed_before_disconnect_dispatch([Values(34, 35)] int inboundCount)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            List<ISession> inbound = [];
            bool[] closing = new bool[inboundCount];
            TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseFirst = new();
            int disconnects = 0;
            for (int i = 0; i < inboundCount; i++)
            {
                int index = i;
                Node node = new(new PrivateKeyGenerator().Generate().PublicKey, "8.8.4.4", 0);
                ISession incoming = CreateSession(node, ConnectionDirection.In);
                incoming.IsClosing.Returns(_ => closing[index]);
                incoming.When(session => session.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>())).Do(_ =>
                {
                    if (Interlocked.Increment(ref disconnects) == 1)
                    {
                        firstEntered.TrySetResult();
                        if (!releaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("reserve-disconnect barrier was not released");
                    }
                    closing[index] = true;
                });
                ctx.PeerManager.OnP2PProtocolInitialized(incoming);
                inbound.Add(incoming);
            }
            ISession firstOutgoing = AddOwnedSession(ctx, ConnectionDirection.Out);
            ISession secondOutgoing = AddOwnedSession(ctx, ConnectionDirection.Out);
            ISession thirdOutgoing = AddOwnedSession(ctx, ConnectionDirection.Out);
            Task first = Task.Run(() => ctx.PeerManager.OnP2PProtocolInitialized(firstOutgoing));
            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ctx.PeerManager.OnP2PProtocolInitialized(secondOutgoing);
                ctx.PeerManager.OnP2PProtocolInitialized(thirdOutgoing);
                Assert.That(Volatile.Read(ref disconnects), Is.EqualTo(inboundCount - 33));
                inbound.Last().Received(1).InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
                if (inboundCount == 35) inbound[^2].Received(1).InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
            }
            finally
            {
                releaseFirst.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(inbound.Take(33).Count(session => session.IsClosing), Is.Zero);
        }

        [Test]
        public async Task Concurrent_outbound_callbacks_displace_only_the_required_active_peers([Values(1, 2)] int overflow)
            => await CheckConcurrentCapacity(overflow, retainedVictim: null);

        [Test]
        public async Task Pending_inbound_disconnect_does_not_release_an_active_opposite_or_awaiting_peer([Values] bool awaiting)
            => await CheckConcurrentCapacity(1, awaiting);

        private static async Task CheckConcurrentCapacity(int overflow, bool? retainedVictim)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(5));
            int inboundCount = retainedVictim.HasValue ? 34 : 33;
            for (int i = 0; i < inboundCount; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
            Session[] inbound = ctx.Sessions.ToArray();
            List<ISession> outbound = [];
            for (int i = 0; i < 50 - inboundCount; i++) outbound.Add(AddOwnedSession(ctx, ConnectionDirection.Out));
            for (int i = 0; i < overflow; i++) AddOwnedSession(ctx, ConnectionDirection.In, privileged: true);
            if (retainedVictim.HasValue)
            {
                Peer retained = ctx.PeerPool.ActivePeers[inbound.Last().Node.Id];
                if (retainedVictim.Value) retained.IsAwaitingConnection = true;
                else retained.OutSession = CreateSession(retained.Node, ConnectionDirection.Out);
            }
            TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim releaseFirst = new();
            int disconnects = 0;
            foreach (Session incoming in inbound)
                incoming.Disconnecting += (_, _) =>
                {
                    if (Interlocked.Increment(ref disconnects) == 1)
                    {
                        firstEntered.TrySetResult();
                        if (!releaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("capacity-disconnect barrier was not released");
                    }
                };
            ctx.PeerManager.Start();
            Task first = Task.Run(() => ctx.PeerManager.OnP2PProtocolInitialized(outbound[0]));
            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ctx.PeerManager.OnP2PProtocolInitialized(outbound[1]);
                Assert.That(Volatile.Read(ref disconnects), Is.EqualTo(retainedVictim.HasValue ? 2 : overflow));
            }
            finally
            {
                releaseFirst.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerPool.ActivePeers.Count, Is.EqualTo(50));
                Assert.That(inbound.Count(session => session.IsClosing), Is.EqualTo(retainedVictim.HasValue ? 2 : overflow));
                if (retainedVictim.HasValue)
                    Assert.That(ctx.PeerPool.ActivePeers.ContainsKey(inbound.Last().Node.Id), Is.True);
                Assert.That(PendingCapacityDisconnects(ctx).Count, Is.Zero);
            }
        }

        [Test]
        public async Task Current_outbound_capacity_victim_is_reserved_without_an_inbound_order()
        {
            await using Context ctx = new(maxActivePeers: 50);
            List<ISession> outbound = [];
            for (int i = 0; i < 50; i++) outbound.Add(AddOwnedSession(ctx, ConnectionDirection.Out));
            AddOwnedSession(ctx, ConnectionDirection.In, privileged: true);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new();
            bool closing = false;
            outbound[0].IsClosing.Returns(_ => closing);
            outbound[0].When(session => session.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>())).Do(_ =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("outbound-disconnect barrier was not released");
                closing = true;
            });
            Task first = Task.Run(() => ctx.PeerManager.OnP2PProtocolInitialized(outbound[0]));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ctx.PeerManager.OnP2PProtocolInitialized(outbound[1]);
                outbound[1].DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
                Assert.That(PendingCapacityDisconnects(ctx).Count, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
            outbound[0].Received(1).InitiateDisconnect(DisconnectReason.TooManyPeers, Arg.Any<string>());
            await ctx.PeerManager.StopAsync();
            Assert.That(PendingCapacityDisconnects(ctx).Count, Is.Zero);
        }

        [Test]
        public async Task Capacity_projection_counts_two_claimed_directions_of_one_peer_once()
        {
            await using Context ctx = new(maxActivePeers: 50);
            ISession incoming = AddOwnedSession(ctx, ConnectionDirection.In);
            Peer owner = ctx.PeerPool.ActivePeers[incoming.Node.Id];
            ISession outgoing = CreateSession(owner.Node, ConnectionDirection.Out);
            owner.OutSession = outgoing;
            for (int i = 1; i < 52; i++) AddOwnedSession(ctx, ConnectionDirection.Out);
            IDictionary pending = PendingCapacityDisconnects(ctx);
            pending.Add(incoming.SessionId, (owner, incoming));
            pending.Add(outgoing.SessionId, (owner, outgoing));
            int projected = (int)typeof(PeerManager).GetMethod("GetProjectedActivePeerCount", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ctx.PeerManager, null)!;
            Assert.That(projected, Is.EqualTo(51));
        }

        [Test]
        public async Task Failed_capacity_disconnect_does_not_leave_a_projected_release([Values] bool throws)
        {
            await using Context ctx = new(maxActivePeers: 1);
            ISession outgoing = AddOwnedSession(ctx, ConnectionDirection.Out);
            AddOwnedSession(ctx, ConnectionDirection.In, privileged: true);
            if (throws)
                outgoing.When(session => session.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>())).Do(_ => throw new InvalidOperationException("disconnect failed"));
            if (throws) Assert.That(() => ctx.PeerManager.OnP2PProtocolInitialized(outgoing), Throws.TypeOf<InvalidOperationException>());
            else ctx.PeerManager.OnP2PProtocolInitialized(outgoing);
            Assert.That(PendingCapacityDisconnects(ctx).Count, Is.Zero);
        }

        private static ISession AddOwnedSession(Context ctx, ConnectionDirection direction, bool privileged = false)
        {
            Node node = new(new PrivateKeyGenerator().Generate().PublicKey, "8.8.4.4", 30303) { IsTrusted = privileged };
            ISession session = CreateSession(node, direction);
            Peer peer = new(node, ctx.Stats.GetOrAdd(node));
            if (direction == ConnectionDirection.In) peer.InSession = session;
            else peer.OutSession = session;
            ctx.PeerPool.ActivePeers.TryAdd(node.Id, peer);
            return session;
        }

        private static ISession CreateSession(Node node, ConnectionDirection direction)
        {
            ISession session = Substitute.For<ISession>();
            session.Node.Returns(node);
            session.RemoteNodeId.Returns(node.Id);
            session.SessionId.Returns(Guid.NewGuid());
            session.Direction.Returns(direction);
            session.State.Returns(SessionState.Initialized);
            return session;
        }

        private static IDictionary PendingCapacityDisconnects(Context ctx)
            => (IDictionary)typeof(PeerManager).GetField("_pendingCapacityDisconnects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.PeerManager)!;

        [Test]
        public async Task Ten_minutes_without_candidates_preserves_ordinary_and_privileged_inbound()
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            SeedEmptyCandidateWindow(ctx);
            clock.Advance(TimeSpan.FromMinutes(10));
            for (int i = 0; i < 48; i++) ctx.RlpxPeer.CreateRandomIncoming();
            CreatePrivilegedInbound(ctx);

            ctx.PeerManager.Start();
            await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(50));
                Assert.That(ctx.Sessions.All(session => !session.IsClosing), Is.True);
                Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.Zero);
            }
        }

        [TestCase("192.168.1.9", "ordinary", false, false, false)]
        [TestCase("192.168.1.9", "ordinary", true, true, false)]
        [TestCase("8.8.4.4", "ordinary", false, false, true)]
        [TestCase("192.168.1.9", "ordinary", true, false, true)]
        [TestCase("192.168.1.9", "static", false, false, true)]
        [TestCase("192.168.1.9", "trusted", false, false, true)]
        [TestCase("192.168.1.9", "bootnode", false, false, true)]
        public async Task Endpoint_policy_controls_reserve_shedding_and_socket_creation(
            string address, string role, bool advertisedPrivate, bool becomesPublic, bool eligible)
            => await CheckEndpointAdmission(address, role, advertisedPrivate, becomesPublic, eligible, addedAfterStart: false);

        [TestCase("192.168.1.9", false)]
        [TestCase("8.8.4.4", true)]
        public async Task Endpoint_policy_controls_candidates_added_after_start(string address, bool eligible)
            => await CheckEndpointAdmission(address, "ordinary", advertisedPrivate: false, becomesPublic: false, eligible, addedAfterStart: true);

        [Test]
        public async Task Endpoint_policy_filters_selection_and_rechecks_candidate_availability([Values] bool refreshDuringSelection)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            IIPResolver resolver = Substitute.For<IIPResolver>();
            IPAddress external = IPAddress.Parse(refreshDuringSelection ? "192.168.1.8" : "8.8.8.8");
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Loopback, external)));
            NetworkConfig config = new()
            {
                LocalIp = "127.0.0.1",
                P2PPort = 0,
                ProcessingThreadCount = 1,
                FilterPeersByRecentIp = false,
                RlpxHostShutdownCloseTimeoutMs = 100
            };
            RlpxHostIntegrationTests.FailingClientChannelFactory channels = new();
            await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver, channelFactory: channels);
            RlpxHost transport = container.Resolve<RlpxHost>();
            IRlpxHost host = Substitute.For<IRlpxHost>();
            Node candidate = new(TestItem.PublicKeyD, "192.168.1.9", 30303);
            bool refreshed = false;
            host.IsEndpointRejected(Arg.Any<Node>()).Returns(call =>
            {
                Node node = call.Arg<Node>();
                bool rejected = transport.IsEndpointRejected(node);
                if (refreshDuringSelection && node.Id == candidate.Id && !refreshed)
                {
                    refreshed = true;
                    external = IPAddress.Parse("8.8.8.8");
                    resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                        new IIPResolver.NethermindIp(IPAddress.Loopback, external));
                }
                return rejected;
            });
            ctx.PeerManager = new PeerManager(host, ctx.PeerPool, ctx.Stats, ctx.NetworkConfig,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance, clock);
            try
            {
                await transport.Init();
                SeedEmptyCandidateWindow(ctx);
                clock.Advance(TimeSpan.FromMinutes(5));
                ctx.PeerPool.GetOrAdd(candidate);
                SelectCandidates(ctx);
                object selection = typeof(PeerManager).GetField("_currentSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.PeerManager)!;
                List<Peer> selected = (List<Peer>)selection.GetType().GetProperty("Candidates")!.GetValue(selection)!;
                bool hasCandidate = (bool)typeof(PeerManager).GetField("_hasDialableCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.PeerManager)!;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(selected.Select(peer => peer.Node.Id), Is.EqualTo(refreshDuringSelection ? new[] { candidate.Id } : []));
                    Assert.That(hasCandidate, Is.False);
                }
                for (int i = 0; i < 50; i++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ctx.PeerManager.ActivePeers, Has.Count.EqualTo(48));
                    Assert.That(await transport.ConnectAsync(candidate), Is.False);
                    Assert.That(channels.CreatedClientChannels, Is.Zero);
                }
            }
            finally
            {
                await transport.Shutdown();
            }
        }

        [Test]
        public async Task Ordinary_candidate_publication_does_not_wait_for_the_session_lock()
        {
            await using Context ctx = new(maxActivePeers: 1);
            AddOwnedSession(ctx, ConnectionDirection.Out);
            ctx.PeerManager.Start();
            Lock sessionLock = (Lock)typeof(PeerManager).GetField("_sessionLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.PeerManager)!;
            using ManualResetEventSlim acquired = new();
            using ManualResetEventSlim release = new();
            Task holder = Task.Run(() =>
            {
                lock (sessionLock)
                {
                    acquired.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("session-lock barrier was not released");
                }
            });
            Task publication = Task.CompletedTask;
            try
            {
                Assert.That(acquired.Wait(TimeSpan.FromSeconds(5)), Is.True);
                publication = Task.Run(() => ctx.PeerPool.GetOrAdd(new Node(TestItem.PublicKeyD, "8.8.8.8", 30303)));
                Assert.That(async () => await publication.WaitAsync(TimeSpan.FromSeconds(2)), Throws.Nothing,
                    "ordinary candidate publication must complete while the session lock is held");
            }
            finally
            {
                release.Set();
                await Task.WhenAll(holder, publication).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static async Task CheckEndpointAdmission(
            string address, string role, bool advertisedPrivate, bool becomesPublic, bool eligible, bool addedAfterStart)
        {
            ManualTimeProvider clock = new();
            await using Context ctx = new(maxActivePeers: 50, timeProvider: clock);
            IIPResolver resolver = Substitute.For<IIPResolver>();
            IPAddress external = IPAddress.Parse(advertisedPrivate ? "192.168.1.8" : "8.8.8.8");
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Loopback, external)));
            NetworkConfig config = new()
            {
                LocalIp = "127.0.0.1",
                P2PPort = 0,
                ProcessingThreadCount = 1,
                FilterPeersByRecentIp = false,
                RlpxHostShutdownCloseTimeoutMs = 100
            };
            RlpxHostIntegrationTests.FailingClientChannelFactory channels = new();
            await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver, channelFactory: channels);
            RlpxHost transport = container.Resolve<RlpxHost>();
            IRlpxHost host = Substitute.For<IRlpxHost>();
            TaskCompletionSource candidateDialCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IsEndpointRejected(Arg.Any<Node>()).Returns(call => transport.IsEndpointRejected(call.Arg<Node>()));
            host.ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>()).Returns(call =>
                transport.ShouldContact(call.ArgAt<IPAddress>(0), call.ArgAt<bool>(1)));
            host.ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>()).Returns(call =>
                ConnectCandidate(call.ArgAt<Node>(0), call.ArgAt<CancellationToken>(1)));
            ctx.RlpxPeer.SessionDisconnected += (sender, session, args) =>
                host.SessionDisconnected += Raise.Event<SessionDisconnectedEventHandler>(sender, session, args);
            ctx.PeerManager = new PeerManager(host, ctx.PeerPool, ctx.Stats, ctx.NetworkConfig,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance, clock);
            try
            {
                await transport.Init();
                SeedEmptyCandidateWindow(ctx);
                clock.Advance(TimeSpan.FromMinutes(5));
                int inboundCount = addedAfterStart ? 2 : 48;
                for (int index = 0; index < inboundCount; index++) ctx.RlpxPeer.CreateRandomIncoming(port: 0);
                Session[] inbound = ctx.Sessions.ToArray();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(inbound.All(session => session.Node.Port == 0), Is.True);
                    Assert.That(inbound.All(session => !ctx.PeerPool.Peers.ContainsKey(session.Node.Id)), Is.True);
                }
                Node candidate = new(TestItem.PublicKeyD, address, 30303)
                {
                    IsStatic = role == "static",
                    IsTrusted = role == "trusted",
                    IsBootnode = role == "bootnode"
                };
                if (addedAfterStart)
                {
                    ctx.PeerManager.Start();
                    await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
                }
                Assert.That(((PeerPool)ctx.PeerPool).TryGetOrAdd(candidate, out _, inboundCandidate: true), Is.True);
                if (becomesPublic)
                {
                    external = IPAddress.Parse("8.8.8.8");
                    resolver.Resolved += Raise.Event<EventHandler<IIPResolver.NethermindIp>>(resolver,
                        new IIPResolver.NethermindIp(IPAddress.Loopback, external));
                }

                if (!addedAfterStart) ctx.PeerManager.Start();
                if (eligible) await candidateDialCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
                await ctx.PeerManager.StopAsync();

                using (Assert.EnterMultipleScope())
                {
                    int shed = inbound.Count(session => session.IsClosing);
                    Assert.That(shed, Is.Zero);
                    Assert.That(inbound.Take(shed).All(session => session.IsClosing), Is.True);
                    Assert.That(inbound.Skip(shed).All(session => !session.IsClosing), Is.True);
                    Assert.That(ctx.PeerManager.ActivePeers.Count(peer => !peer.Node.IsStatic && !peer.Node.IsTrusted && peer.InSession is { IsClosing: false }),
                        Is.EqualTo(inboundCount - shed));
                    Assert.That(channels.CreatedClientChannels, Is.EqualTo(eligible ? 1 : 0));
                    if (eligible)
                    {
                        host.Received().ShouldContact(candidate.Address.Address, Arg.Any<bool>());
                        _ = host.Received(1).ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>());
                        _ = host.Received(1).ConnectAsync(candidate, Arg.Any<CancellationToken>());
                    }
                    else
                    {
                        host.DidNotReceive().ShouldContact(Arg.Any<IPAddress>(), Arg.Any<bool>());
                        _ = host.DidNotReceive().ConnectAsync(Arg.Any<Node>(), Arg.Any<CancellationToken>());
                    }
                }
            }
            finally
            {
                await ctx.PeerManager.StopAsync();
                await transport.Shutdown();
            }

            async Task<bool> ConnectCandidate(Node node, CancellationToken cancellationToken)
            {
                try
                {
                    bool connected = await transport.ConnectAsync(node, cancellationToken);
                    if (node.Id.Equals(TestItem.PublicKeyD))
                    {
                        candidateDialCompleted.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    return connected;
                }
                finally
                {
                    if (node.Id.Equals(TestItem.PublicKeyD)) candidateDialCompleted.TrySetResult();
                }
            }
        }

        private sealed class ObservingTimeProvider(TimeProvider inner, Action observeTimestamp, Action<TimeSpan>? observeTimer = null) : TimeProvider
        {
            public override long TimestampFrequency => inner.TimestampFrequency;
            public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
            public override long GetTimestamp()
            {
                observeTimestamp();
                return inner.GetTimestamp();
            }
            public override System.Threading.ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                System.Threading.ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
                observeTimer?.Invoke(dueTime);
                return timer;
            }
        }

        private static IPeerPool CreateForwardingPool(Context ctx)
        {
            IPeerPool pool = Substitute.For<IPeerPool>();
            pool.Peers.Returns(ctx.PeerPool.Peers);
            pool.ActivePeers.Returns(ctx.PeerPool.ActivePeers);
            pool.StaticPeers.Returns(_ => ctx.PeerPool.StaticPeers);
            pool.PeerCount.Returns(_ => ctx.PeerPool.PeerCount);
            pool.ActivePeerCount.Returns(_ => ctx.PeerPool.ActivePeerCount);
            pool.GetOrAdd(Arg.Any<Node>()).Returns(call => ctx.PeerPool.GetOrAdd(call.Arg<Node>()));
            pool.TryGetOrAdd(Arg.Any<Node>(), out Arg.Any<Peer?>(), Arg.Any<bool>()).Returns(call =>
            {
                bool admitted = ctx.PeerPool.TryGetOrAdd(call.ArgAt<Node>(0), out Peer? peer, call.ArgAt<bool>(2));
                call[1] = peer;
                return admitted;
            });
            pool.TryGet(Arg.Any<PublicKey>(), out Arg.Any<Peer>()).Returns(call =>
            {
                bool found = ctx.PeerPool.TryGet(call.ArgAt<PublicKey>(0), out Peer peer);
                call[1] = peer;
                return found;
            });
            return pool;
        }

        private static void ReplacePeerManager(Context ctx, IPeerPool pool, TimeProvider? clock = null)
            => ctx.PeerManager = new PeerManager(ctx.RlpxPeer, pool, ctx.Stats, ctx.NetworkConfig,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance, clock ?? TimeProvider.System);

        private static Task ObserveFailedDialRemoval(Context ctx, Node node, TimeProvider? clock = null)
        {
            TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IPeerPool pool = CreateForwardingPool(ctx);
            pool.Peers.Returns(_ =>
            {
                if (ctx.Stats.GetOrAdd(node).DidEventHappen(NodeStatsEventType.ConnectionFailed)
                    && !ctx.PeerPool.ActivePeers.ContainsKey(node.Id))
                    removed.TrySetResult();
                return ctx.PeerPool.Peers;
            });
            ReplacePeerManager(ctx, pool, clock);
            return removed.Task;
        }

        private static void CreatePrivilegedInbound(Context ctx)
        {
            foreach (Node privileged in new[]
            {
                new Node(TestItem.PublicKeyB, "1.2.3.4", 30303) { IsStatic = true },
                new Node(TestItem.PublicKeyC, "1.2.3.4", 30303) { IsTrusted = true }
            })
            {
                ctx.PeerPool.GetOrAdd(privileged);
                ctx.RlpxPeer.CreateIncoming(CreateIncomingSessionTemplate(privileged.Id));
            }
        }

        private static void SeedEmptyCandidateWindow(Context ctx)
        {
            Node seed = new(TestItem.PublicKeyB, "8.8.8.8", 30303);
            ctx.PeerPool.GetOrAdd(seed);
            SelectCandidates(ctx);
            ctx.PeerPool.TryRemove(seed.Id, out _);
            SelectCandidates(ctx);
        }

        private static async Task<Session> ConnectOwnedOutgoing(Context ctx, Node node)
        {
            Peer peer = ctx.PeerPool.GetOrAdd(node);
            ctx.PeerPool.ActivePeers.TryAdd(node.Id, peer);
            Assert.That(await ctx.RlpxPeer.ConnectAsync(node), Is.True);
            Session session = ctx.Sessions.Last();
            peer.OutSession = session;
            session.Handshake(node.Id);
            InitSession(session);
            return session;
        }

        private static void SelectCandidates(Context ctx)
            => typeof(PeerManager).GetMethod("SelectAndRankCandidates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ctx.PeerManager, null);

        [Test]
        public async Task Repeated_local_failures_back_off_and_log_once_until_recovery([Values] bool recover)
        {
            await using UpdateRecoveryContext context = new(fail: true);
            int[] expectedSeconds = [1, 2, 4, 8, 16, 32, 60, 60];
            TimeSpan? last = null;
            Task timerCreated = context.Clock.TimerCreated;
            foreach (int expected in expectedSeconds)
            {
                if (last.HasValue) context.Clock.AdvanceAndFireTimer(last.Value);
                await timerCreated.WaitAsync(TimeSpan.FromSeconds(5));
                last = context.Clock.DueTimes[^1];
                timerCreated = context.Clock.NextTimerCreated;
                Assert.That(last, Is.EqualTo(TimeSpan.FromSeconds(expected)));
            }
            context.Logger.Received(1).Error("Peer update loop failure", Arg.Any<Exception>());
            context.Logger.Received(7).Debug(Arg.Is<string>(text => text.StartsWith("Peer update loop failure")));
            if (recover)
            {
                context.Fail = false;
                context.Clock.AdvanceAndFireTimer(last!.Value);
                await timerCreated.WaitAsync(TimeSpan.FromSeconds(5));
                TimeSpan idle = context.Clock.DueTimes[^1];
                timerCreated = context.Clock.NextTimerCreated;
                Assert.That(idle, Is.EqualTo(TimeSpan.FromSeconds(1)));
                context.Fail = true;
                context.Clock.AdvanceAndFireTimer(idle);
                await timerCreated.WaitAsync(TimeSpan.FromSeconds(5));
                TimeSpan freshFailure = context.Clock.DueTimes[^1];
                Assert.That(freshFailure, Is.EqualTo(TimeSpan.FromSeconds(1)));
                context.Logger.Received(2).Error("Peer update loop failure", Arg.Any<Exception>());
            }
        }

        [Test]
        public async Task A_healthy_empty_pool_waits_without_reporting_failure()
        {
            await using UpdateRecoveryContext context = new(fail: false);
            await context.Clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
            TimeSpan idle = context.Clock.DueTimes[^1];
            Assert.That(idle, Is.EqualTo(TimeSpan.FromSeconds(1)));
            context.Logger.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
        }

        private sealed class UpdateRecoveryContext : IAsyncDisposable
        {
            private readonly PeerManager _manager;
            private int _fail;
            public bool Fail { set => Volatile.Write(ref _fail, value ? 1 : 0); }
            public ManualTimeProvider Clock { get; } = new();
            public InterfaceLogger Logger { get; } = Substitute.For<InterfaceLogger>();

            public UpdateRecoveryContext(bool fail)
            {
                Fail = fail;
                Logger.IsError.Returns(true);
                Logger.IsDebug.Returns(true);
                IPeerPool pool = Substitute.For<IPeerPool>();
                pool.Peers.Returns(new ConcurrentDictionary<PublicKeyAsKey, Peer>());
                pool.ActivePeers.Returns(new ConcurrentDictionary<PublicKeyAsKey, Peer>());
                pool.PeerCount.Returns(_ => Volatile.Read(ref _fail) != 0 ? throw new InvalidOperationException("local peer store failure") : 0);
                _manager = new PeerManager(Substitute.For<IRlpxHost>(), pool, Substitute.For<INodeStatsManager>(),
                    new NetworkConfig { NumConcurrentOutgoingConnects = 1, PeersUpdateInterval = 60000 },
                    new Enode(TestItem.PublicKeyA, System.Net.IPAddress.Loopback, 30303),
                    new OneLoggerLogManager(new ILogger(Logger)), Clock);
                _manager.Start();
            }

            public async ValueTask DisposeAsync() => await _manager.StopAsync();
        }
    }
}
