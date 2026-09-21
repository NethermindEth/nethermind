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
        Node staticPeer = CreateNode();
        Node other = CreateNode();
        Node local = CreateNode();
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
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

            await staticPeer.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);
            await WaitUntilAsync(() => peerManager.PeerCount == 1, token, "the static peer's inbound session was never admitted");
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(other.P2P), token), Is.True);

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
        Node remote = CreateNode();
        Node local = CreateNode();
        SetMatchingStatus(remote, local);
        // Distinguishable from our own identify literal, so the field provably carries what the remote sent.
        remote.P2P.IdentifySettingsForTest.AgentVersion = "test-remote/inbound-1.2.3";

        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

            await remote.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);

            await WaitUntilAsync(() => peerManager.PeerCount == 1, token, "the manager never admitted the session the remote opened");
            IPeerDirectory directory = peerManager;
            PeerRecord record = directory.Peers.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(record.PeerId, Is.EqualTo(remote.P2P.LocalPeerId!.ToString()));
                Assert.That(record.Direction, Is.EqualTo(PeerDirection.Inbound), "the remote dialed us: reporting it as Outbound is the lie this closes");
                Assert.That(record.State, Is.EqualTo(PeerConnectionState.Connected));
                Assert.That(record.LastKnownMultiaddr, Does.Contain("127.0.0.1"));
                Assert.That(record.AgentVersion, Is.EqualTo("test-remote/inbound-1.2.3"), "the identify agent string the remote advertised, not null and not our own");
                // A session the remote opened was never discovered by us, so there is no ENR to
                // attribute - fabricating one here would be worse than reporting the honest gap.
                Assert.That(record.Enr, Is.Null);
            }

            Assert.That(directory.TryGetPeer(record.PeerId, out PeerRecord lookedUp), Is.True);
            Assert.That(lookedUp.Direction, Is.EqualTo(PeerDirection.Inbound));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Dialing_a_peer_that_already_connected_to_us_reuses_its_session_and_keeps_it_inbound(CancellationToken token)
    {
        // BeaconP2P.DialPeerAsync hands back the existing session for an already-connected peer id, so
        // a static/discovery dial of a peer that got in first must neither record it twice nor relabel
        // it as Outbound.
        Node remote = CreateNode();
        Node local = CreateNode();
        SetMatchingStatus(remote, local);

        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

            await remote.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);
            await WaitUntilAsync(() => peerManager.PeerCount == 1, token, "the inbound session was never admitted");

            // Straight at the libp2p layer: the manager's own dial path short-circuits on "already
            // connected" before it ever dials, so only a direct dial exercises the session reuse.
            ISession reused = await local.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(remote.P2P)), token);

            Assert.That(local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out ISession? established), Is.True);
            Assert.That(reused, Is.SameAs(established), "the dial must hand back the session the remote opened, not open a second one");
            Assert.That((await local.P2P.GetSessionInfoAsync(reused, token)).Direction, Is.EqualTo(PeerDirection.Inbound));
            Assert.That(local.P2P.SessionCountForTest, Is.EqualTo(1), "one connection, not a second outbound one");

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.True, "already connected counts as success");

            IPeerDirectory directory = peerManager;
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "one session, one entry");
            Assert.That(directory.Peers.Single().Direction, Is.EqualTo(PeerDirection.Inbound));
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

            await knocking.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);

            string knockingId = knocking.P2P.LocalPeerId!.ToString();
            // The knocking side losing its session proves the refusal ran to its disconnect, so the
            // record check below cannot pass merely by looking before the refusal happened.
            await WaitUntilAsync(() => knocking.P2P.SessionCountForTest == 0, token, "the refused session was not torn down");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 1, token, "the refused session was not torn down");
            using (Assert.EnterMultipleScope())
            {
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

            await knocking.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);
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

            await banned.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);

            await WaitUntilAsync(() => peerManager.GetPeerDiagnostics().Single(d => d.PeerId == bannedId).LastDisconnectReason == "Banned", token, "the refusal was never recorded");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the refused session was not torn down");
            Assert.That(peerManager.PeerCount, Is.EqualTo(0), "a ban must hold against a peer that connects to us, not only against our own dials");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_session_the_remote_opened_whose_status_exchange_fails_is_closed_not_left_open(CancellationToken token)
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

            await remote.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(local.P2P)), token);

            // Both status versions were refused over the open session, so the admission provably threw
            // after the session existed: a count of zero below cannot be the pre-connect zero.
            await WaitUntilAsync(() => refusing.Requests >= 2, token, "the status exchange never reached the remote");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session whose status exchange failed was left open and uncounted");
            Assert.That(peerManager.PeerCount, Is.EqualTo(0));
        }
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
            PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);

            Assert.That(refusing.Requests, Is.GreaterThanOrEqualTo(2), "the status exchange never reached the remote");
            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session whose status exchange failed was left open and uncounted");
            Assert.That(peerManager.PeerCount, Is.EqualTo(0));
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
    public async Task Legacy_ReportFailure_overload_still_classifies_a_dead_session_as_fatal(CancellationToken token)
    {
        // Preserves the exact substring rule the single-overload method used to apply inline, for
        // callers this change could not reach (RangeSync.cs) - see crossStreamRisks in the report.
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

            long before = FailureCount("SessionClosed");
            peer.ReportFailure("Blocks-by-range [1, 17) failed: Channel closed unexpectedly");
            long after = FailureCount("SessionClosed");

            Assert.That(after - before, Is.EqualTo(1), "a 'Channel closed' free-text reason must still classify as SessionClosed");
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

    private static PeerManager NewManagerWithoutSessions(int faultDisconnectsBeforeBan)
    {
        Node node = CreateNode();
        node.Config.FaultDisconnectsBeforeBan = faultDisconnectsBeforeBan;
        return new PeerManager(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);
    }

    private static void SetMatchingStatus(params Node[] nodes)
    {
        byte[] forkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(AnchorSlot));
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

    private static string LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return address;
    }

    private record Node(BeaconP2P P2P, BeaconChainStatusHolder StatusHolder, BeaconChainConfig Config);

    /// <param name="statusSource">What the node serves over <c>status</c>; defaults to its own settable holder.</param>
    private static Node CreateNode(IBeaconChainStatusSource? statusSource = null)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        BeaconP2P p2p = new(config, Spec, store, statusSource ?? statusHolder, metadataSource, new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
        return new Node(p2p, statusHolder, config);
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
    }
}
