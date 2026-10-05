// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

[CancelAfter(120_000)]
public class PeerAdmissionRaceTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TimerTolerance = TimeSpan.FromMilliseconds(16);

    public enum DialOutcome
    {
        DialFails,
        CallerCancels,
        TimesOut,
    }

    [Test]
    public async Task A_dial_that_fails_hands_back_the_session_the_peer_opened_meanwhile_but_not_after_the_caller_cancels(
        [Values(DialOutcome.DialFails, DialOutcome.CallerCancels)] DialOutcome outcome, CancellationToken token)
    {
        Node local = Create();
        Node remote = Create();
        using TcpListener blackhole = new(IPAddress.Loopback, 0);
        blackhole.Start();
        await using PeerHostScope hosts = new(local.P2P, remote.P2P);
        await hosts.StartAsync(token, local.P2P, remote.P2P);
        int port = ((IPEndPoint)blackhole.LocalEndpoint).Port;
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);

        // The address names the remote but leads to a socket that never speaks, so the dial is stuck past its pre-check.
        Task<ISession> dial = local.P2P.DialPeerAsync(Multiaddress.Decode($"/ip4/127.0.0.1/tcp/{port}/p2p/{remote.P2P.LocalPeerId}"), caller.Token);
        using Socket stalled = await blackhole.AcceptSocketAsync(token);
        await DialAsync(remote.P2P, local.P2P, token);
        await WaitUntilAsync(() => local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out _), "the remote's session never reached the local node", token);
        Assert.That(dial.IsCompleted, Is.False, "fixture: the dial is still in flight");

        if (outcome == DialOutcome.DialFails)
        {
            stalled.Close();
            ISession returned = await dial.WaitAsync(Hold, token);
            local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out ISession? established);
            Assert.That(returned, Is.SameAs(established), "the failed dial hands back the session the peer opened");
        }
        else
        {
            await caller.CancelAsync();
            Assert.CatchAsync<OperationCanceledException>(() => dial.WaitAsync(Hold, token), "the caller's cancellation propagates even though a session exists");
        }
    }

    [Test]
    public async Task A_session_the_peer_opened_during_our_dial_to_it_is_admitted_unless_the_caller_cancelled([Values] DialOutcome outcome, CancellationToken token)
    {
        ScriptedStatusSource served = new(static _ => Status);
        Node local = Create();
        Node remote = Create(served);
        using TcpListener blackhole = new(IPAddress.Loopback, 0);
        blackhole.Start();
        await using PeerHostScope hosts = new(local.P2P, remote.P2P);
        await hosts.StartAsync(token, local.P2P, remote.P2P);
        PeerManager peerManager = local.CreatePeerManager();
        // Subscribed after the manager, so it runs once the manager has seen the session.
        using ManualResetEventSlim eventSeen = new();
        local.P2P.SessionEstablished += (_, _) => eventSeen.Set();
        int port = ((IPEndPoint)blackhole.LocalEndpoint).Port;
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<bool> dial = peerManager.TryAddPeerAsync($"/ip4/127.0.0.1/tcp/{port}/p2p/{remote.P2P.LocalPeerId}", caller.Token);
        using Socket stalled = await blackhole.AcceptSocketAsync(token);
        await DialAsync(remote.P2P, local.P2P, token);
        if (!eventSeen.Wait(Hold, token))
        {
            Assert.Fail("the remote's session never reached the local node");
        }

        Assert.That(dial.IsCompleted || peerManager.PeerCount != 0, Is.False, "fixture: the dial holds the reservation and the event skipped the session");

        switch (outcome)
        {
            case DialOutcome.DialFails:
                stalled.Close();
                await dial.WaitAsync(Hold, token);
                break;
            case DialOutcome.CallerCancels:
                await caller.CancelAsync();
                Assert.CatchAsync<OperationCanceledException>(() => dial.WaitAsync(Hold, token));
                break;
            case DialOutcome.TimesOut:
                Assert.That(await dial.WaitAsync(TimeSpan.FromSeconds(20), token), Is.False, "the dial timed out");
                break;
        }

        if (outcome == DialOutcome.CallerCancels)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(served.Requests, Is.Zero, "no status exchange after the caller cancelled");
                Assert.That(peerManager.PeerCount, Is.Zero, "no admission after the caller cancelled");
            }

            return;
        }

        await WaitUntilAsync(() => peerManager.PeerCount == 1, $"the peer's session was left unadmitted ({local.P2P.SessionCountForTest} open)", token, TimeSpan.FromSeconds(5));
        Assert.That(local.P2P.SessionCountForTest, Is.EqualTo(1));
    }

    [Test]
    [Repeat(8)]
    public async Task Peers_dialing_each_other_at_once_end_with_one_session_on_both_sides(CancellationToken token)
    {
        Node first = Create();
        Node second = Create();
        await using PeerHostScope hosts = new(first.P2P, second.P2P);
        await hosts.StartAsync(token, first.P2P, second.P2P);
        PeerManager firstManager = first.CreatePeerManager();
        PeerManager secondManager = second.CreatePeerManager();
        using Barrier bothReady = new(2);

        Task<bool> DialAtBarrier(PeerManager manager, BeaconP2P target) => Task.Run(() =>
        {
            bothReady.SignalAndWait(Hold, token);
            return manager.TryAddPeerAsync(LoopbackAddressText(target), token);
        }, token);

        Stopwatch dialing = Stopwatch.StartNew();
        bool[] dialed = await Task.WhenAll(DialAtBarrier(firstManager, second.P2P), DialAtBarrier(secondManager, first.P2P));
        if (dialing.Elapsed >= IdentifyAgentVersionProbe.ReadTimeout)
        {
            throw new TimeoutException($"the dials took {dialing.Elapsed}");
        }

        bool Settled() => firstManager.PeerCount == 1 && secondManager.PeerCount == 1 && first.P2P.SessionCountForTest == 1 && second.P2P.SessionCountForTest == 1;
        await WaitUntilAsync(Settled, $"dials {dialed[0]}/{dialed[1]} left peers {firstManager.PeerCount}/{secondManager.PeerCount}, sessions {first.P2P.SessionCountForTest}/{second.P2P.SessionCountForTest}", token, TimeSpan.FromSeconds(3));
        Assert.That(dialed, Has.Some.True, "at least one dial admitted the peer");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_redial_the_peer_refuses_while_it_still_holds_our_old_session_is_admitted_once_that_session_closes(CancellationToken token)
    {
        (byte[] lowerKey, byte[] higherKey) = OrderedKeys();
        PeerId lower = PeerIdOf(lowerKey);
        Node local = Create(privateKey: lowerKey);
        Node remote = Create(privateKey: higherKey);
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(BeaconP2P.CreateLibp2pLoggerFactory(LimboLogs.Instance))
            .AddLibp2p(static builder => builder)
            .BuildServiceProvider();
        await using ILocalPeer oldSession = services.GetRequiredService<IPeerFactory>().Create(BeaconP2P.IdentityFromStoredKey(lowerKey));
        await using PeerHostScope hosts = new(local.P2P, remote.P2P);
        await hosts.StartAsync(token, local.P2P, remote.P2P);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(local.P2P.LocalPeerId, Is.EqualTo(lower), "fixture: the node runs on the given key");
            Assert.That(string.CompareOrdinal(local.P2P.LocalPeerId!.ToString(), remote.P2P.LocalPeerId!.ToString()), Is.Negative, "fixture: our node is the side that redials");
        }

        await DialFromPlainPeerAsync(oldSession, remote.P2P, token);
        await WaitUntilAsync(() => remote.P2P.TryGetEstablishedSession(lower, out _), "fixture: the old session never reached the peer", token);

        await using Relay relay = Relay.Start(PortOf(remote.P2P), closeFirst: 0);
        PeerManager manager = local.CreatePeerManager();
        Task<bool> dial = manager.TryAddPeerAsync(relay.AddressOf(remote.P2P), token);
        await relay.WhenEndedAsync(connection: 2).WaitAsync(Hold, token);
        await oldSession.DisposeAsync();

        bool admitted = await dial.WaitAsync(Hold, token);
        TimeSpan[] accepted = relay.Accepted;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admitted, Is.True, "a redial after the old session closed admits the peer");
        Assert.That(manager.PeerCount, Is.EqualTo(1));
        for (int redial = 1; redial < accepted.Length; redial++)
        {
            // Timer resolution can end a delay a tick early.
            Assert.That(accepted[redial] - accepted[redial - 1], Is.GreaterThanOrEqualTo(PeerManager.RedialBackoff * redial - TimerTolerance),
                $"redial {redial} waited out its backoff");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_closed_session_is_not_redialed_by_the_gossip_router(CancellationToken token)
    {
        Node local = Create();
        Node remote = Create();
        await using PeerHostScope hosts = new(local.P2P, remote.P2P);
        await hosts.StartAsync(token, local.P2P, remote.P2P);
        remote.P2P.Discover([LoopbackAddress(local.P2P)]);
        await WaitUntilAsync(() => remote.P2P.RoutingStateForTest!.ConnectedPeers.Contains(local.P2P.LocalPeerId!), "fixture: the router never connected to the peer", token);
        Assert.That(remote.P2P.TryGetEstablishedSession(local.P2P.LocalPeerId!, out ISession? session), Is.True, "fixture: the router's session is established");

        await session!.DisconnectAsync();
        await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0 && remote.P2P.SessionCountForTest == 0, "fixture: the closed session was not torn down", token);
        // Past the library's default ReconnectionPeriod of 15 s.
        await Task.Delay(TimeSpan.FromSeconds(20), token);

        Assert.That((local.P2P.SessionCountForTest, remote.P2P.SessionCountForTest), Is.EqualTo((0, 0)), "neither router dialed the closed peer again");
    }

    private static int PortOf(BeaconP2P node) => int.Parse(LoopbackAddressText(node, withPeerId: false).Split('/')[4]);

    private sealed class Relay : IAsyncDisposable
    {
        private readonly TcpListener _front = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<TimeSpan> _accepted = [];
        private readonly List<TaskCompletionSource> _ended = [];
        private readonly List<IDisposable> _open = [];
        private Task _accepting = Task.CompletedTask;

        public static Relay Start(int port, int closeFirst)
        {
            Relay relay = new();
            relay._front.Start();
            relay._accepting = relay.AcceptAsync(port, closeFirst, relay._stop.Token);
            return relay;
        }

        public TimeSpan[] Accepted
        {
            get
            {
                lock (_accepted)
                {
                    return [.. _accepted];
                }
            }
        }

        public string AddressOf(BeaconP2P node) => $"/ip4/127.0.0.1/tcp/{((IPEndPoint)_front.LocalEndpoint).Port}/p2p/{node.LocalPeerId}";
        public Task WhenEndedAsync(int connection) => EndedOf(connection - 1).Task;

        private TaskCompletionSource EndedOf(int index)
        {
            lock (_accepted)
            {
                while (_ended.Count <= index)
                {
                    _ended.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _ended[index];
            }
        }

        private async Task AcceptAsync(int port, int closeFirst, CancellationToken token)
        {
            try
            {
                for (int index = 0; ; index++)
                {
                    Socket inbound = await _front.AcceptSocketAsync(token);
                    lock (_accepted)
                    {
                        _accepted.Add(_clock.Elapsed);
                    }

                    if (index < closeFirst)
                    {
                        inbound.Close();
                        EndedOf(index).TrySetResult();
                        continue;
                    }

                    Socket outbound = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await outbound.ConnectAsync(IPAddress.Loopback, port, token);
                    NetworkStream inboundStream = new(inbound, ownsSocket: true);
                    NetworkStream outboundStream = new(outbound, ownsSocket: true);
                    lock (_accepted)
                    {
                        _open.Add(inboundStream);
                        _open.Add(outboundStream);
                    }

                    TaskCompletionSource ended = EndedOf(index);
                    _ = Task.WhenAny(inboundStream.CopyToAsync(outboundStream, token), outboundStream.CopyToAsync(inboundStream, token))
                        .ContinueWith(_ =>
                        {
                            // A close on one side must reach the other, or the dialer waits on a connection the peer has dropped.
                            inboundStream.Dispose();
                            outboundStream.Dispose();
                            ended.TrySetResult();
                        }, TaskScheduler.Default);
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _front.Stop();
            await _accepting;
            lock (_accepted)
            {
                _open.ForEach(static stream => stream.Dispose());
            }

            _stop.Dispose();
        }
    }

    /// <summary>A pair of keys whose peer ids order as named; only the side with the lower peer id redials.</summary>
    private static (byte[] Lower, byte[] Higher) OrderedKeys()
    {
        byte[] first = RandomNumberGenerator.GetBytes(32);
        byte[] second = RandomNumberGenerator.GetBytes(32);
        return string.CompareOrdinal(PeerIdOf(first).ToString(), PeerIdOf(second).ToString()) < 0 ? (first, second) : (second, first);
    }

    private static PeerId PeerIdOf(byte[] privateKey) => BeaconP2P.IdentityFromStoredKey(privateKey).PeerId;

    [Test]
    public async Task A_slow_teardown_of_a_failed_dial_does_not_hold_the_dial_slot(CancellationToken token)
    {
        using ManualResetEventSlim refuse = new();
        using ManualResetEventSlim teardownEntered = new();
        using ManualResetEventSlim teardownRelease = new();
        ScriptedStatusSource served = new(_ =>
        {
            refuse.Wait(Hold);
            throw new Eth2ReqRespException("status refused for the test");
        });
        Node refusing = Create(served);
        Node other = Create();
        Node local = Create();
        local.Config.MaxConcurrentOutboundDials = 1;
        await using PeerHostScope hosts = new(local.P2P, refusing.P2P, other.P2P);
        await hosts.StartAsync(token, refusing.P2P, other.P2P, local.P2P);
        PeerManager peerManager = local.CreatePeerManager();

        Task<bool> failing = peerManager.TryAddPeerAsync(LoopbackAddressText(refusing.P2P), token);
        try
        {
            await WaitUntilAsync(() => served.Requests >= 1, "the failing dial never exchanged status", token);
            Assert.That(local.P2P.TryGetEstablishedSession(refusing.P2P.LocalPeerId!, out ISession? session), Is.True);
            // Disconnecting cancels this token, which runs its callbacks inline: the teardown takes as long as this one.
            ((LocalPeer.Session)session!).ConnectionToken.Register(() =>
            {
                teardownEntered.Set();
                teardownRelease.Wait(TimeSpan.FromSeconds(30));
            });
            refuse.Set();
            Assert.That(teardownEntered.Wait(Hold, token), Is.True, "the failed dial never tore its session down");

            bool added = await peerManager.TryAddPeerAsync(LoopbackAddressText(other.P2P), token);
            Assert.That(added, Is.True, "the next dial ran while the teardown was still in progress");
        }
        finally
        {
            teardownRelease.Set();
        }

        Assert.That(await failing, Is.False);
    }
}
