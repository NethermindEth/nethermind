// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The target peer band (watermarks, trimming, ban list) and the outbound dial cap: the only
/// defence available with no gossipsub scoring in the consumed libp2p library.
/// </summary>
public class PeerBandTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private const ulong AnchorSlot = 13_410_304;

    [TestCase("/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAm", ExpectedResult = "16Uiu2HAm")]
    [TestCase("/ip4/1.2.3.4/tcp/9000", ExpectedResult = "/ip4/1.2.3.4/tcp/9000")] // no /p2p/: the whole address is the identity
    [TestCase("", ExpectedResult = "")]
    public string Peer_id_is_extracted_from_the_p2p_multiaddr_component(string address) =>
        PeerManager.ExtractPeerIdForTest(address);

    /// <summary>
    /// A peer dropped for timing out is routine, and log watchers treat an exception type name in an Info line as a
    /// crash, so the drop line names the cause instead.
    /// </summary>
    [TestCase(typeof(TaskCanceledException), ExpectedResult = "request timed out")]
    [TestCase(typeof(OperationCanceledException), ExpectedResult = "request timed out")]
    [TestCase(typeof(TimeoutException), ExpectedResult = "request timed out")]
    [TestCase(typeof(ReqRespTimeoutException), ExpectedResult = "timed out after 16 s waiting for the channel to open: the request budget ran out")]
    [TestCase(typeof(InvalidOperationException), ExpectedResult = "the peer sent garbage")]
    public string A_drop_names_its_cause_rather_than_the_exception_type(Type exceptionType) =>
        PeerManager.DescribeFailure(exceptionType == typeof(InvalidOperationException) ? new InvalidOperationException("the peer sent garbage")
            : exceptionType == typeof(ReqRespTimeoutException) ? new ReqRespTimeoutException("timed out after 16 s waiting for the channel to open: the request budget ran out")
            : (Exception)Activator.CreateInstance(exceptionType)!);

    [Test]
    public void A_single_fault_disconnect_below_the_threshold_does_not_ban()
    {
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 3);

        manager.RecordDisconnect("peerA", messagesSent: 10, failuresReported: 1, GoodbyeReason.Fault, "repeated failures");

        Assert.That(manager.IsBannedForTest("peerA"), Is.False);
    }

    [Test]
    public void Consecutive_fault_disconnects_reaching_the_threshold_ban_the_peer_id()
    {
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 3);

        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures");
        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures");
        Assert.That(manager.IsBannedForTest("peerA"), Is.False, "two of three should not ban yet");

        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures");
        Assert.That(manager.IsBannedForTest("peerA"), Is.True, "the third consecutive fault must ban");
    }

    [Test]
    public void A_non_fault_disconnect_resets_the_consecutive_fault_streak()
    {
        // A fork-digest mismatch near a BPO rotation is not misbehaviour: it must not count toward,
        // or survive as, a fault streak that a later unrelated fault could otherwise complete.
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 2);

        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures");
        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.IrrelevantNetwork, "fork digest mismatch");
        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures");

        Assert.That(manager.IsBannedForTest("peerA"), Is.False, "the reset streak is only one fault long, not two");
    }

    [Test]
    public void Diagnostics_report_the_ban_and_disconnect_history_of_a_peer_that_is_not_connected()
    {
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 1);

        manager.RecordDisconnect("peerA", messagesSent: 7, failuresReported: 2, GoodbyeReason.Fault, "repeated failures");

        PeerManager.PeerDiagnostics diagnostics = manager.GetPeerDiagnostics().Single(d => d.PeerId == "peerA");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics.Connected, Is.False);
            Assert.That(diagnostics.Banned, Is.True);
            Assert.That(diagnostics.DisconnectCount, Is.EqualTo(1));
            Assert.That(diagnostics.MessagesSent, Is.EqualTo(7));
            Assert.That(diagnostics.FailuresReported, Is.EqualTo(2));
            Assert.That(diagnostics.LastDisconnectReason, Is.EqualTo("Fault"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Dial_cap_refuses_a_new_peer_once_the_high_watermark_is_reached(CancellationToken token)
    {
        Node server1 = CreateNode();
        Node server2 = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server1, server2, client);
        client.Config.MaxPeerCount = 1;

        await using (client.P2P)
        await using (server1.P2P)
        await using (server2.P2P)
        {
            await server1.P2P.StartAsync(token);
            await server2.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server1.P2P), token), Is.True);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1));

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server2.P2P), token), Is.False,
                "already at MaxPeerCount: must refuse without attempting the second dial");
            Assert.That(peerManager.PeerCount, Is.EqualTo(1));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Maintenance_round_trims_the_worst_peer_once_over_the_high_watermark(CancellationToken token)
    {
        Node worseServer = CreateNode();
        Node betterServer = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(worseServer, betterServer, client);
        worseServer.StatusHolder.CurrentStatus.HeadSlot = AnchorSlot;
        betterServer.StatusHolder.CurrentStatus.HeadSlot = AnchorSlot + 100;

        await using (client.P2P)
        await using (worseServer.P2P)
        await using (betterServer.P2P)
        {
            await worseServer.P2P.StartAsync(token);
            await betterServer.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(worseServer.P2P), token), Is.True);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(betterServer.P2P), token), Is.True);
            Assert.That(peerManager.PeerCount, Is.EqualTo(2));

            // Lower the high watermark below the already-connected count, as a config-driven band
            // change or a dial-race overshoot would: the next maintenance round must trim back down.
            client.Config.MaxPeerCount = 1;
            client.Config.TargetPeerCount = 1;
            await peerManager.RunMaintenanceRoundAsync(token);

            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "must trim down to the target");
            IBeaconSyncPeer remaining = peerManager.GetBestPeers(0).Single();
            Assert.That(remaining.HeadSlot, Is.EqualTo(AnchorSlot + 100), "the peer with the lower head slot is the worst and should be trimmed");

            PeerManager.PeerDiagnostics trimmed = peerManager.GetPeerDiagnostics().Single(d => d.HeadSlot != AnchorSlot + 100 && d.DisconnectCount > 0);
            Assert.That(trimmed.LastDisconnectReason, Is.EqualTo("TooManyPeers"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_peer_that_connected_to_us_first_is_still_exempt_from_trimming(CancellationToken token)
    {
        // An inbound session is keyed by the address the remote came from, never by its configured
        // static address, so an exemption matched on the address string would trim the static peer.
        AdmissionWatch watch = new();
        Node staticPeer = CreateNode();
        Node other = CreateNode();
        Node local = CreateNode(logManager: watch.LogManager);
        SetMatchingStatus(staticPeer, other, local);
        staticPeer.StatusHolder.CurrentStatus.HeadSlot = AnchorSlot;
        other.StatusHolder.CurrentStatus.HeadSlot = AnchorSlot + 100;

        await using (local.P2P)
        await using (staticPeer.P2P)
        await using (other.P2P)
        {
            await staticPeer.P2P.StartAsync(token);
            await other.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            local.Config.StaticPeers = LoopbackAddress(staticPeer.P2P);
            PeerManager peerManager = watch.Watch(local, staticPeer, other);

            await PeerSessionNodes.DialAsync(staticPeer.P2P, local.P2P, token);
            await watch.AdmittedAsync("the static peer's inbound session was never admitted", token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1));
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(other.P2P), token), Is.True, () => watch.Describe("the second peer was not admitted"));

            local.Config.MaxPeerCount = 1;
            local.Config.TargetPeerCount = 1;
            await peerManager.RunMaintenanceRoundAsync(token);

            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "must trim down to the target");
            Assert.That(peerManager.GetBestPeers(0).Single().HeadSlot, Is.EqualTo(AnchorSlot),
                "the static peer is the worse one by head slot and must still be the one kept");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_banned_static_peer_is_not_reconnected_even_though_it_is_reachable(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            string address = LoopbackAddress(server.P2P);
            client.Config.StaticPeers = address;
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);

            // Ban the id ahead of time, the way three real consecutive fault disconnects would have
            // left it (RecordDisconnect's own accumulation is covered directly above): the address
            // stays perfectly reachable, so a plain connectivity failure cannot explain a refusal here.
            string peerId = PeerManager.ExtractPeerIdForTest(address);
            peerManager.RecordDisconnect(peerId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            peerManager.RecordDisconnect(peerId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            peerManager.RecordDisconnect(peerId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            Assert.That(peerManager.IsBannedForTest(peerId), Is.True, "test setup: three faults must have banned it already");

            await peerManager.RunMaintenanceRoundAsync(token);

            Assert.That(peerManager.PeerCount, Is.EqualTo(0), "the static-peer reconnect loop must not dial a banned id");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Concurrent_dials_cannot_overshoot_the_configured_peer_band_ceiling(CancellationToken token)
    {
        // Three servers dialed concurrently against a ceiling of one: every dial's admission check
        // races the others before any of them has inserted into the pool, which is exactly the
        // check-then-act window that let MaxConcurrentOutboundDials-many concurrent dials overshoot.
        Node server1 = CreateNode();
        Node server2 = CreateNode();
        Node server3 = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server1, server2, server3, client);
        client.Config.MaxPeerCount = 1;

        await using (client.P2P)
        await using (server1.P2P)
        await using (server2.P2P)
        await using (server3.P2P)
        {
            await server1.P2P.StartAsync(token);
            await server2.P2P.StartAsync(token);
            await server3.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);

            Task<bool>[] dials =
            [
                peerManager.TryAddPeerAsync(LoopbackAddress(server1.P2P), token),
                peerManager.TryAddPeerAsync(LoopbackAddress(server2.P2P), token),
                peerManager.TryAddPeerAsync(LoopbackAddress(server3.P2P), token),
            ];
            bool[] results = await Task.WhenAll(dials);

            Assert.That(results.Count(r => r), Is.EqualTo(1), "only one of the three concurrent dials may be admitted once MaxPeerCount=1 is reached");
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "the configured maximum must not be overshot by concurrent dials racing the check");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Static_peer_reconnect_cannot_take_the_pool_past_the_peer_band_ceiling(CancellationToken token)
    {
        // Two reachable static peers against a ceiling of one. The static reconnect loop used to dial
        // straight through ConnectAsync with no ceiling check at all, and TrimToPeerBandAsync exempts
        // static peers, so nothing ever brought the count back down: a permanent overshoot.
        Node server1 = CreateNode();
        Node server2 = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server1, server2, client);
        client.Config.MaxPeerCount = 1;
        client.Config.TargetPeerCount = 1;

        await using (client.P2P)
        await using (server1.P2P)
        await using (server2.P2P)
        {
            await server1.P2P.StartAsync(token);
            await server2.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            client.Config.StaticPeers = $"{LoopbackAddress(server1.P2P)},{LoopbackAddress(server2.P2P)}";
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);

            await peerManager.RunMaintenanceRoundAsync(token);

            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "a static reconnect must go through the same ceiling reservation as a discovery dial");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_session_the_remote_opened_is_admitted_and_reported_as_inbound_with_its_agent_string(CancellationToken token)
    {
        AdmissionWatch watch = new();
        Node remote = CreateNode();
        Node local = CreateNode(logManager: watch.LogManager);
        SetMatchingStatus(remote, local);
        // Distinguishable from our own identify literal, so the field provably carries what the remote sent.
        remote.P2P.IdentifySettingsForTest.AgentVersion = "test-remote/inbound-1.2.3";

        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            // Admission failures are logged at Debug only; the watch keeps that log so a failure names why the session was dropped.
            PeerManager peerManager = watch.Watch(local, remote);

            await PeerSessionNodes.DialAsync(remote.P2P, local.P2P, token);

            await watch.AdmittedAsync("the manager never admitted the session the remote opened", token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), () => watch.Describe("the admitted peer does not match"));
            IPeerDirectory directory = peerManager;
            PeerRecord record = directory.Peers.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(record.PeerId, Is.EqualTo(remote.P2P.LocalPeerId!.ToString()), () => watch.Describe("the admitted peer does not match"));
                Assert.That(record.Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the remote dialed us: reporting it as Outbound is the lie this closes"));
                Assert.That(record.State, Is.EqualTo(PeerConnectionState.Connected), () => watch.Describe("the admitted peer does not match"));
                Assert.That(record.LastKnownMultiaddr, Does.Contain("127.0.0.1"), () => watch.Describe("the admitted peer does not match"));
                Assert.That(record.AgentVersion, Is.EqualTo("test-remote/inbound-1.2.3"), () => watch.Describe("the identify agent string the remote advertised, not null and not our own"));
                // A session the remote opened was never discovered by us, so there is no ENR to
                // attribute - fabricating one here would be worse than reporting the honest gap.
                Assert.That(record.Enr, Is.Null, () => watch.Describe("the admitted peer does not match"));
            }

            Assert.That(directory.TryGetPeer(record.PeerId, out PeerRecord lookedUp), Is.True, () => watch.Describe("the admitted peer does not match"));
            Assert.That(lookedUp.Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the admitted peer does not match"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Dialing_a_peer_that_already_connected_to_us_reuses_its_session_and_keeps_it_inbound(CancellationToken token)
    {
        // BeaconP2P.DialPeerAsync hands back the existing session for an already-connected peer id, so
        // a static/discovery dial of a peer that got in first must neither record it twice nor relabel
        // it as Outbound.
        AdmissionWatch watch = new();
        Node remote = CreateNode();
        Node local = CreateNode(logManager: watch.LogManager);
        SetMatchingStatus(remote, local);

        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = watch.Watch(local, remote);

            await PeerSessionNodes.DialAsync(remote.P2P, local.P2P, token);
            await watch.AdmittedAsync("the inbound session was never admitted", token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), () => watch.Describe("the admitted peer does not match"));

            // Straight at the libp2p layer: the manager's own dial path short-circuits on "already
            // connected" before it ever dials, so only a direct dial exercises the session reuse.
            ISession reused = await local.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(remote.P2P)), token);

            Assert.That(local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out ISession? established), Is.True, () => watch.Describe("the admitted peer does not match"));
            Assert.That(reused, Is.SameAs(established), () => watch.Describe("the dial must hand back the session the remote opened, not open a second one"));
            Assert.That((await local.P2P.GetSessionInfoAsync(reused, token)).Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the admitted peer does not match"));
            Assert.That(local.P2P.SessionCountForTest, Is.EqualTo(1), () => watch.Describe("one connection, not a second outbound one"));

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.True, () => watch.Describe("already connected counts as success"));

            IPeerDirectory directory = peerManager;
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), () => watch.Describe("one session, one entry"));
            Assert.That(directory.Peers.Single().Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the admitted peer does not match"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_session_the_remote_opened_at_the_peer_band_ceiling_is_refused_and_torn_down(CancellationToken token)
    {
        Node dialed = CreateNode();
        Node knocking = CreateNode();
        Node local = CreateNode();
        SetMatchingStatus(dialed, knocking, local);
        local.Config.MaxPeerCount = 1;

        await using (local.P2P)
        await using (dialed.P2P)
        await using (knocking.P2P)
        {
            await dialed.P2P.StartAsync(token);
            await knocking.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(dialed.P2P), token), Is.True);

            using BeaconP2P.SessionWatch knockingSessions = local.P2P.WatchSessions(knocking.P2P.LocalPeerId!);
            await PeerSessionNodes.DialToBeRefusedAsync(knocking.P2P, local.P2P, token);

            string knockingId = knocking.P2P.LocalPeerId!.ToString();
            // The knocking side losing its session proves the refusal ran to its disconnect, so the
            // record check below cannot pass merely by looking before the refusal happened.
            await WaitUntilAsync(() => knocking.P2P.SessionCountForTest == 0, token, "the refused session was not torn down");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 1, token, "the refused session was not torn down");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(knockingSessions.Opened, Is.EqualTo(1), "the knocking session reached this node, so the refusal path ran");
                Assert.That(peerManager.PeerCount, Is.EqualTo(1), "an inbound session must not take the pool past MaxPeerCount");
                Assert.That(peerManager.GetPeerDiagnostics().Any(d => d.PeerId == knockingId), Is.False,
                    "a never-admitted id must not get a record: distinct knockers at the ceiling would otherwise evict real peers' history");
            }
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_refusal_at_the_peer_band_ceiling_leaves_the_consecutive_fault_streak_untouched(CancellationToken token)
    {
        Node dialed = CreateNode();
        Node knocking = CreateNode();
        Node local = CreateNode();
        SetMatchingStatus(dialed, knocking, local);
        local.Config.MaxPeerCount = 1;
        local.Config.FaultDisconnectsBeforeBan = 2;

        await using (local.P2P)
        await using (dialed.P2P)
        await using (knocking.P2P)
        {
            await dialed.P2P.StartAsync(token);
            await knocking.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);
            string knockingId = knocking.P2P.LocalPeerId!.ToString();
            peerManager.RecordDisconnect(knockingId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(dialed.P2P), token), Is.True);

            await PeerSessionNodes.DialToBeRefusedAsync(knocking.P2P, local.P2P, token);
            await WaitUntilAsync(() => peerManager.GetPeerDiagnostics().Single(d => d.PeerId == knockingId).LastDisconnectReason == "TooManyPeers", token, "the refusal was never recorded");

            // Being turned away while we are full says nothing about the peer's behaviour: the fault
            // before it and the fault after it must still add up to the threshold of two.
            peerManager.RecordDisconnect(knockingId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            Assert.That(peerManager.IsBannedForTest(knockingId), Is.True);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_banned_peer_that_connects_to_us_is_refused(CancellationToken token)
    {
        Node banned = CreateNode();
        Node local = CreateNode();
        SetMatchingStatus(banned, local);

        await using (local.P2P)
        await using (banned.P2P)
        {
            await banned.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

            string bannedId = banned.P2P.LocalPeerId!.ToString();
            peerManager.RecordDisconnect(bannedId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            peerManager.RecordDisconnect(bannedId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            peerManager.RecordDisconnect(bannedId, 0, 0, GoodbyeReason.Fault, "repeated failures");
            Assert.That(peerManager.IsBannedForTest(bannedId), Is.True, "test setup: three faults must have banned it already");

            await PeerSessionNodes.DialToBeRefusedAsync(banned.P2P, local.P2P, token);

            await WaitUntilAsync(() => peerManager.GetPeerDiagnostics().Single(d => d.PeerId == bannedId).LastDisconnectReason == "Banned", token, "the refusal was never recorded");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the refused session was not torn down");
            Assert.That(peerManager.PeerCount, Is.EqualTo(0), "a ban must hold against a peer that connects to us, not only against our own dials");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public Task A_session_the_remote_opened_whose_status_exchange_fails_is_closed_not_left_open(CancellationToken token) =>
        StatusExchangeFailsAsync(token);

    private static async Task<bool> StatusExchangeFailsAsync(CancellationToken token)
    {
        RefusingStatusSource refusing = new();
        Node remote = CreateNode(refusing);
        Node local = CreateNode();
        SetMatchingStatus(local);

        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

            await PeerSessionNodes.DialToBeRefusedAsync(remote.P2P, local.P2P, token);

            // Both status versions were refused over the open session, so the admission provably threw
            // after the session existed: a count of zero below cannot be the pre-connect zero.
            await PeerSessionNodes.WaitUntilAsync(() => refusing.Requests >= 2, "the status exchange never reached the remote", token);
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session whose status exchange failed was left open and uncounted");
            Assert.That(peerManager.PeerCount, Is.EqualTo(0));
        }

        return true;
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_dialed_session_whose_status_exchange_fails_is_closed_not_left_open(CancellationToken token)
    {
        RefusingStatusSource refusing = new();
        Node remote = CreateNode(refusing);
        Node local = CreateNode();
        SetMatchingStatus(local);

        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            ManualTimestamper clock = new();
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: clock);

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);

            Assert.That(refusing.Requests, Is.GreaterThanOrEqualTo(2), "the status exchange never reached the remote");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session whose status exchange failed was left open and uncounted");
            Assert.That(peerManager.PeerCount, Is.EqualTo(0));
            int requests = refusing.Requests;
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);
            Assert.That(refusing.Requests, Is.EqualTo(requests), "a failed endpoint is not immediately dialled again");
            clock.Add(TimeSpan.FromMinutes(15));
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);
            Assert.That(refusing.Requests, Is.GreaterThan(requests), "an expired backoff permits another dial");
        }
    }

    /// <summary>Polls a condition with a short cadence; the caller's token bounds the wait.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token, string failure)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (bounded.IsCancellationRequested)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }

    [Test]
    public async Task Admission_capacity_wait_returns_immediately_below_target()
    {
        Node node = CreateNode();
        node.Config.TargetPeerCount = 5;
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await peerManager.WaitForAdmissionCapacityAsync(cts.Token);

        Assert.That(cts.IsCancellationRequested, Is.False, "an empty pool below the target must not wait at all");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Admission_capacity_wait_blocks_once_the_pool_is_at_target(CancellationToken token)
    {
        // BeaconSyncOrchestrator's discovery dial loop used to decide this for itself by comparing
        // PeerCount to config directly; it now asks PeerManager, which must give the same backpressure.
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token), Is.True);
            client.Config.TargetPeerCount = 1;

            using CancellationTokenSource shortLived = new(TimeSpan.FromMilliseconds(300));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, shortLived.Token);

            // CatchAsync, not ThrowsAsync: Task.Delay throws the derived TaskCanceledException, and
            // ThrowsAsync requires an exact type match.
            Assert.CatchAsync<OperationCanceledException>(async () => await peerManager.WaitForAdmissionCapacityAsync(linked.Token),
                "at the target watermark the wait must not return on its own");
        }
    }

    [Test]
    public void Maintenance_runs_more_often_while_under_the_low_watermark()
    {
        Node node = CreateNode();
        node.Config.MinPeerCount = 20;
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);

        TimeSpan underPeered = peerManager.NextMaintenanceIntervalForTest;

        node.Config.MinPeerCount = 0; // an empty pool (PeerCount 0) is no longer "under" this watermark
        TimeSpan atWatermark = peerManager.NextMaintenanceIntervalForTest;

        Assert.That(underPeered, Is.LessThan(atWatermark), "MinPeerCount must actually change behaviour, not just be read into nothing");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task ReportFailure_reason_is_a_bounded_metric_label_not_the_free_text_detail(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token), Is.True);
            IBeaconSyncPeer peer = peerManager.GetBestPeers(0).Single();

            long before = FailureCount("ProtocolViolation");
            peer.ReportFailure(PeerFailureReason.ProtocolViolation, "some unbounded detail nobody should turn into a label: " + Guid.NewGuid());
            long after = FailureCount("ProtocolViolation");

            Assert.That(after - before, Is.EqualTo(1), "the closed-cardinality reason, not the free-text detail, is the metric label");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Peers_surface_reports_peer_id_direction_state_multiaddr_agent_and_enr_for_a_connected_peer(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);
        server.P2P.IdentifySettingsForTest.AgentVersion = "test-remote/outbound-4.5.6";
        const string discoveredEnr = "enr:-discovered-test-record";

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            string address = LoopbackAddress(server.P2P);
            string expectedPeerId = PeerManager.ExtractPeerIdForTest(address);
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            // Passed the way the discovery dial loop passes it (see BeaconSyncOrchestrator.DialCandidateAsync),
            // not the plain two-arg overload a static-peer reconnect uses.
            Assert.That(await peerManager.TryAddPeerAsync(address, token, discoveredEnr), Is.True);

            IPeerDirectory directory = peerManager;
            PeerRecord record = directory.Peers.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(record.PeerId, Is.EqualTo(expectedPeerId));
                Assert.That(record.Direction, Is.EqualTo(PeerDirection.Outbound));
                Assert.That(record.State, Is.EqualTo(PeerConnectionState.Connected));
                // Not just "non-empty": must be the real loopback remote address, not a placeholder
                // or the pre-connect dial address (which uses 0.0.0.0, not 127.0.0.1, before rewrite).
                Assert.That(record.LastKnownMultiaddr, Does.Contain("127.0.0.1"));
                Assert.That(record.AgentVersion, Is.EqualTo("test-remote/outbound-4.5.6"), "the identify agent string the server advertised, not null and not our own");
                // The Beacon API's node/peers endpoint reads this straight off the record; dropping it
                // here silently regresses that endpoint back to reporting null for a discovered peer.
                Assert.That(record.Enr, Is.EqualTo(discoveredEnr));
            }

            Assert.That(directory.TryGetPeer(expectedPeerId, out PeerRecord lookedUp), Is.True);
            Assert.That(lookedUp.PeerId, Is.EqualTo(expectedPeerId));
            Assert.That(lookedUp.Enr, Is.EqualTo(discoveredEnr));
        }
    }

    [Test]
    public void TryGetPeer_refuses_an_unknown_peer_id_rather_than_matching_anything()
    {
        Node node = CreateNode();
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);
        IPeerDirectory directory = peerManager;

        Assert.That(directory.TryGetPeer("no-such-peer", out _), Is.False);
    }

    [Test]
    public void TryGetPeer_refuses_an_empty_id_even_when_an_unresolved_dialing_address_would_otherwise_match_it()
    {
        // An address with no /p2p/ component extracts to itself, not "" - the only way a tracked
        // entry's derived peer id is ever "" is a raw "" address, reached here directly since a real
        // dial to "" fails and clears its reservation before a test could observe it.
        Node node = CreateNode();
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);
        peerManager.ReserveDialingForTest("");
        IPeerDirectory directory = peerManager;

        Assert.That(directory.TryGetPeer("", out _), Is.False, "an empty id must never be treated as a wildcard, even when a raw '' address is technically tracked");
    }

    [Test]
    public void Drops_for_silence_alone_never_ban_a_peer_that_answered_before()
    {
        // A slow peer is not a hostile one: banning it starves the pool while few peers are usable.
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 3);

        for (int i = 0; i < 10; i++)
        {
            manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures, last: request timed out", unresponsive: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.IsBannedForTest("peerA"), Is.False);
            Assert.That(manager.GetPeerDiagnostics().Single(d => d.PeerId == "peerA").DisconnectCount, Is.EqualTo(10), "the drops stay in the diagnostics");
        }
    }

    [Test]
    public void A_drop_for_silence_does_not_excuse_the_violations_around_it()
    {
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 3);

        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "invalid response");
        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "repeated failures, last: request timed out", unresponsive: true);
        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "invalid response");
        Assert.That(manager.IsBannedForTest("peerA"), Is.False, "two violations are below the threshold");

        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "invalid response");
        Assert.That(manager.IsBannedForTest("peerA"), Is.True, "three violations ban the peer however many timeouts sit between them");
    }

    [Test]
    public void A_ban_ends_after_the_configured_time_and_the_peer_then_starts_from_a_clean_streak()
    {
        ManualTimestamper time = new();
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 3, time);
        for (int i = 0; i < 3; i++)
        {
            manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "invalid response");
        }

        Assert.That(manager.IsBannedForTest("peerA"), Is.True, "test setup: three violations ban the peer");
        time.Add(TimeSpan.FromMinutes(new BeaconChainConfig().PeerBanMinutes) - TimeSpan.FromSeconds(1));
        Assert.That(manager.IsBannedForTest("peerA"), Is.True, "the ban holds until its time is up");

        time.Add(TimeSpan.FromSeconds(1));
        Assert.That(manager.GetPeerDiagnostics().Single(d => d.PeerId == "peerA").Banned, Is.False, "the diagnostics must not report a ban that has run out");
        Assert.That(manager.IsBannedForTest("peerA"), Is.False, "the ban must end once its time is up");

        manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.Fault, "invalid response");
        Assert.That(manager.IsBannedForTest("peerA"), Is.False, "the old streak must not carry over: one violation after the ban is not three");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_banned_static_peer_is_reconnected_once_its_ban_has_ended(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);

        await using (client.P2P)
        await using (server.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);

            string address = LoopbackAddress(server.P2P);
            client.Config.StaticPeers = address;
            ManualTimestamper time = new();
            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, timestamper: time);
            string peerId = PeerManager.ExtractPeerIdForTest(address);
            for (int i = 0; i < client.Config.FaultDisconnectsBeforeBan; i++)
            {
                peerManager.RecordDisconnect(peerId, 0, 0, GoodbyeReason.Fault, "invalid response");
            }

            await peerManager.RunMaintenanceRoundAsync(token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(0), "test setup: the ban must hold before it ends");

            time.Add(TimeSpan.FromMinutes(client.Config.PeerBanMinutes));
            await peerManager.RunMaintenanceRoundAsync(token);

            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "a peer whose ban has ended is admitted like any other");
        }
    }

    [Test]
    public void The_ban_table_stays_within_its_cap_when_every_entry_is_banned()
    {
        ManualTimestamper time = new();
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 1, time);

        const int capacity = 8192;
        for (int i = 0; i < capacity; i++)
        {
            manager.RecordDisconnect($"peer-{i}", 0, 0, GoodbyeReason.Fault, "invalid response");
            time.Add(TimeSpan.FromMilliseconds(1));
        }

        manager.RecordDisconnect("peer-new", 0, 0, GoodbyeReason.Fault, "invalid response");

        HashSet<string> remaining = [.. manager.GetPeerDiagnostics().Select(d => d.PeerId)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Count, Is.EqualTo(capacity), "a table of active bans must not grow past the cap");
            Assert.That(remaining.Contains("peer-0"), Is.False, "the ban closest to running out is the one that goes");
            Assert.That(remaining.Contains("peer-new"), Is.True);
        }
    }

    [Test]
    public void A_fault_during_a_ban_does_not_extend_it()
    {
        ManualTimestamper time = new();
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 1, time);
        TimeSpan banTime = TimeSpan.FromMinutes(new BeaconChainConfig().PeerBanMinutes);
        manager.RecordDisconnect("peerA", 0, 0, GoodbyeReason.Fault, "invalid response");
        time.Add(banTime / 2);

        manager.RecordDisconnect("peerA", 0, 0, GoodbyeReason.Fault, "invalid response");
        time.Add(banTime / 2);

        Assert.That(manager.IsBannedForTest("peerA"), Is.False, "the ban ends when the first ban's time is up, a later fault must not restart it");
    }

    [TestCase(int.MinValue, false)]
    [TestCase(-1, false)]
    [TestCase(0, false)]
    [TestCase(int.MaxValue, true)]
    public void An_out_of_range_ban_duration_does_not_throw(int banMinutes, bool expectedBanned)
    {
        Node node = CreateNode();
        node.Config.FaultDisconnectsBeforeBan = 1;
        node.Config.PeerBanMinutes = banMinutes;
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);

        Assert.DoesNotThrow(() => manager.RecordDisconnect("peerA", 0, 0, GoodbyeReason.Fault, "invalid response"));
        Assert.That(manager.IsBannedForTest("peerA"), Is.EqualTo(expectedBanned));
    }

    [Test]
    public void A_full_table_evicts_the_oldest_entry_that_is_not_banned_before_any_active_ban()
    {
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 2);
        manager.RecordDisconnect("peer-0", 0, 0, GoodbyeReason.Fault, "invalid response");
        manager.RecordDisconnect("peer-0", 0, 0, GoodbyeReason.Fault, "invalid response");
        for (int i = 1; i < 8192; i++)
        {
            manager.RecordDisconnect($"peer-{i}", 0, 0, GoodbyeReason.Fault, "invalid response");
        }

        manager.RecordDisconnect("peer-new", 0, 0, GoodbyeReason.Fault, "invalid response");

        HashSet<string> remaining = [.. manager.GetPeerDiagnostics().Select(d => d.PeerId)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.IsBannedForTest("peer-0"), Is.True, "a flood of new ids must not push an active ban out of the table");
            Assert.That(remaining.Contains("peer-1"), Is.False, "the oldest entry that is not banned goes instead");
            Assert.That(remaining.Count, Is.EqualTo(8192));
        }
    }

    [Test]
    public void A_full_table_treats_an_expired_ban_as_an_ordinary_entry()
    {
        ManualTimestamper time = new();
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 2, time);
        manager.RecordDisconnect("peer-0", 0, 0, GoodbyeReason.Fault, "invalid response");
        manager.RecordDisconnect("peer-0", 0, 0, GoodbyeReason.Fault, "invalid response");
        time.Add(TimeSpan.FromMinutes(new BeaconChainConfig().PeerBanMinutes));
        for (int i = 1; i < 8192; i++)
        {
            manager.RecordDisconnect($"peer-{i}", 0, 0, GoodbyeReason.Fault, "invalid response");
        }

        manager.RecordDisconnect("peer-new", 0, 0, GoodbyeReason.Fault, "invalid response");

        HashSet<string> remaining = [.. manager.GetPeerDiagnostics().Select(d => d.PeerId)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Contains("peer-0"), Is.False, "a ban that has run out protects nothing, and this entry is the oldest");
            Assert.That(remaining.Contains("peer-1"), Is.True);
        }
    }

    [Test]
    public void The_ban_diagnostics_table_evicts_the_oldest_non_banned_entry_once_over_capacity()
    {
        // A real (deterministic) bound, not "some arbitrary entry from undefined dictionary order":
        // fill the table to its cap, then confirm specifically the FIRST-created id is the one gone,
        // and every later one survived.
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 1000);

        const int capacity = 8192;
        for (int i = 0; i < capacity; i++)
        {
            manager.RecordDisconnect($"peer-{i}", 0, 0, GoodbyeReason.Fault, "repeated failures");
        }

        Assert.That(manager.GetPeerDiagnostics().Count, Is.EqualTo(capacity));

        // One more entry pushes the table over its cap and must evict the oldest (peer-0).
        manager.RecordDisconnect("peer-new", 0, 0, GoodbyeReason.Fault, "repeated failures");

        HashSet<string> remaining = [.. manager.GetPeerDiagnostics().Select(d => d.PeerId)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Contains("peer-0"), Is.False, "the oldest tracked non-banned id must be the one evicted");
            Assert.That(remaining.Contains("peer-1"), Is.True, "only the oldest entry is evicted, not an arbitrary one");
            Assert.That(remaining.Contains("peer-new"), Is.True);
            Assert.That(remaining.Count, Is.EqualTo(capacity));
        }
    }

    private static long FailureCount(string reason) =>
        Metrics.BeaconChainPeerFailuresByReason.TryGetValue(new StringLabel(reason), out long count) ? count : 0;

    private static PeerManager NewManagerWithoutSessions(int faultDisconnectsBeforeBan, ITimestamper? timestamper = null)
    {
        Node node = CreateNode();
        node.Config.FaultDisconnectsBeforeBan = faultDisconnectsBeforeBan;
        return new PeerManager(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: timestamper);
    }

    internal static void SetMatchingStatus(params Node[] nodes)
    {
        byte[] forkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(Spec.GetSlotAtTime((ulong)Timestamper.Default.UnixTime.Seconds)));
        foreach (Node node in nodes)
        {
            node.StatusHolder.CurrentStatus = new StatusMessageV2
            {
                ForkDigest = forkDigest,
                FinalizedRoot = Hash256.Zero,
                FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
                HeadRoot = Hash256.Zero,
                HeadSlot = AnchorSlot,
                EarliestAvailableSlot = AnchorSlot,
            };
        }
    }

    internal static string LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return address;
    }

    internal record Node(BeaconP2P P2P, BeaconChainStatusHolder StatusHolder, BeaconChainConfig Config, BeaconChainStore Store, LocalMetadataSource Metadata);

    /// <param name="statusSource">What the node serves over <c>status</c>; defaults to its own settable holder.</param>
    /// <param name="logManager">Where the node's P2P host logs; silent by default.</param>
    internal static Node CreateNode(IBeaconChainStatusSource? statusSource = null, ILogManager? logManager = null)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        BeaconP2P p2p = new(config, Spec, store, statusSource ?? statusHolder, metadataSource,
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), logManager ?? LimboLogs.Instance);
        return new Node(p2p, statusHolder, config, store, metadataSource);
    }

    /// <summary>
    /// Waits for the manager's admission event instead of polling its count, and keeps the Debug log of the manager and
    /// the local host: an admission that never happens is logged there and the session silently dropped.
    /// </summary>
    private sealed class AdmissionWatch : InterfaceLogger
    {
        private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

        private readonly List<string> _lines = [];
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Node[] _nodes = [];

        public AdmissionWatch() => LogManager = new OneLoggerLogManager(new ILogger(this));

        public ILogManager LogManager { get; }

        public PeerManager Watch(Node local, params Node[] others)
        {
            _nodes = [local, .. others];
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LogManager);
            manager.PeerAdmitted += _ => _admitted.TrySetResult();
            return manager;
        }

        /// <summary>Returns once a peer was admitted; fails with the logged cause when none is within <see cref="HangBound"/>.</summary>
        public async Task AdmittedAsync(string failure, CancellationToken token)
        {
            try
            {
                await _admitted.Task.WaitAsync(HangBound, token);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                Assert.Fail(Describe(failure));
            }
        }

        /// <summary>The failure text with the nodes' session and identify-timeout counts and the captured log.</summary>
        public string Describe(string failure)
        {
            string state = string.Join("; ", _nodes.Select(static (n, i) => $"node {i}: sessions={n.P2P.SessionCountForTest} identifyTimeouts={n.P2P.IdentifyTimeoutsForTest}"));
            string[] lines;
            lock (_lines)
            {
                lines = [.. _lines];
            }

            return $"{failure} ({state}). Log:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }

        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => false;
        public bool IsError => true;

        public void Info(string text) => Add(text);
        public void Warn(string text) => Add(text);
        public void Debug(string text) => Add(text);
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) => Add(ex is null ? text : $"{text}: {ex}");

        private void Add(string text)
        {
            lock (_lines)
            {
                _lines.Add(text);
            }
        }
    }

    /// <summary>Answers every <c>status</c> request with an error chunk, so a status exchange with this
    /// node fails only after the session is already open and identified.</summary>
    private sealed class RefusingStatusSource : IBeaconChainStatusSource
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public StatusMessageV2 CurrentStatus
        {
            get
            {
                Interlocked.Increment(ref _requests);
                throw new Eth2ReqRespException("status refused for the test");
            }
        }

        // Unused here: this double exists to fail the status exchange, and nothing on the peer
        // path reads the API's view of the chain.
        public Hash256 JustifiedRoot => Hash256.Zero;

        public bool ExecutionInSync => false;
    }
}
