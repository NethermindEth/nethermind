// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerSelectionOrderTests
{
    // The sort throws on an inconsistent comparator, which needs more than 16 peers; a failure reported mid-sort must not reach it.
    [Test]
    public void The_order_is_taken_from_one_reading_of_each_peer_so_a_change_mid_sort_cannot_break_the_sort([Range(0, 400, 20)] int changeAfterReads)
    {
        const int peerCount = 20;
        int[] peers = [.. Enumerable.Range(0, peerCount)];
        ulong[] heads = new ulong[peerCount];
        heads[0] = 100;
        heads[8] = 50;
        int reads = 0;
        int[] readsPerPeer = new int[peerCount];
        bool changed() => reads++ >= changeAfterReads;

        int[] ordered = null!;
        Assert.DoesNotThrow(() => ordered = PeerManager.OrderForSelection(
            peers,
            peer => { readsPerPeer[peer]++; return peer == 0 && changed(); },
            peer => { readsPerPeer[peer]++; return peer == 1 && changed() ? 1 : 0; },
            peer => { readsPerPeer[peer]++; return heads[peer]; }));

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ordered, Is.EquivalentTo(peers));
        Assert.That(readsPerPeer, Has.All.EqualTo(3), "one reading of the cooldown, one of the requests in flight and one of the head slot per peer");
    }

    [Test]
    public void A_peer_in_cooldown_is_listed_after_all_others_and_the_rest_by_head_slot()
    {
        int[] peers = [0, 1, 2, 3, 4];
        ulong[] heads = [500, 40, 30, 20, 10];

        int[] ordered = PeerManager.OrderForSelection(peers, static peer => peer is 0 or 2, static _ => 0, peer => heads[peer]);

        Assert.That(ordered, Is.EqualTo(new List<int> { 1, 3, 4, 0, 2 }));
    }

    [Test]
    public void Among_peers_not_in_cooldown_the_one_with_fewer_requests_in_flight_comes_first_whatever_its_head_slot()
    {
        int[] peers = [0, 1, 2, 3, 4];
        ulong[] heads = [500, 40, 30, 20, 10];
        int[] inFlight = [2, 0, 1, 0, 0];

        int[] ordered = PeerManager.OrderForSelection(peers, static peer => peer is 4, peer => inFlight[peer], peer => heads[peer]);

        Assert.That(ordered, Is.EqualTo(new List<int> { 1, 3, 2, 0, 4 }), "idle by head slot, then busier, and a peer in cooldown last even when idle");
    }

    [TestCase(0UL, "Sync peers for any head: 0 usable; left out 1 without status")]
    [TestCase(5UL, "Sync peers for head slot 5: 0 usable; left out 1 without status")]
    public async Task The_selection_log_names_the_head_asked_for_or_any_head(ulong minHeadSlot, string expected)
    {
        TestLogRecorder logs = new();
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, logs);
        manager.AddPeerForTest(Substitute.For<ISession>(), "/ip4/10.0.0.1/tcp/9000/p2p/16Uiu2HAmPeer");

        manager.GetBestPeers(minHeadSlot);

        Assert.That(logs.Lines.Select(static l => l.Text), Has.Some.StartsWith(expected));
    }
}
