// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
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

    private static Node CreateNode()
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        BeaconP2P p2p = new(config, Spec, store, statusHolder, metadataSource, new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
        return new Node(p2p, statusHolder, config);
    }
}
