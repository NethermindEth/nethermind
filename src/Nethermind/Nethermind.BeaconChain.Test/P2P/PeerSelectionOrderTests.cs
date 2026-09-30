// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.P2P;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>Peer selection runs while other loops report failures and receive statuses, so what it sorts by must not change under the sort.</summary>
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
            peer => { readsPerPeer[peer]++; return heads[peer]; }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ordered, Is.EquivalentTo(peers));
            Assert.That(readsPerPeer, Has.All.EqualTo(2), "one reading of the cooldown and one of the head slot per peer");
        }
    }

    [Test]
    public void A_peer_in_cooldown_is_listed_after_all_others_and_the_rest_by_head_slot()
    {
        int[] peers = [0, 1, 2, 3, 4];
        ulong[] heads = [500, 40, 30, 20, 10];

        int[] ordered = PeerManager.OrderForSelection(peers, static peer => peer is 0 or 2, peer => heads[peer]);

        Assert.That(ordered, Is.EqualTo(new List<int> { 1, 3, 4, 0, 2 }));
    }
}
