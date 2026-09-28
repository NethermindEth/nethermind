// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// fulu/das-core.md: a node must retrieve every column it samples, and a peer custodies the columns of
/// <c>get_custody_groups(node_id, custody_group_count)</c> for the node id and <c>cgc</c> of its record, so a candidate
/// custodying more of the wanted columns is dialed first.
/// </summary>
public class CustodyRankedCandidatesTests
{
    private const int Capacity = 4;

    [Test]
    public void Candidates_custodying_more_wanted_columns_are_taken_first_and_ties_in_arrival_order()
    {
        CustodyRankedCandidates candidates = new(Capacity);
        ulong[] wanted = [1, 2, 3];
        candidates.Add(Candidate("none", 9), wanted);
        candidates.Add(Candidate("one", 1), wanted);
        candidates.Add(Candidate("supernode", Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).ToArray()), wanted);
        candidates.Add(Candidate("another-one", 2), wanted);

        Assert.That(TakeAll(candidates, wanted), Is.EqualTo(new[] { "supernode", "one", "another-one", "none" }));
    }

    [Test]
    public void Without_a_wanted_column_candidates_are_taken_in_arrival_order()
    {
        CustodyRankedCandidates candidates = new(Capacity);
        candidates.Add(Candidate("first", 9), []);
        candidates.Add(Candidate("second", [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c)]), []);

        Assert.That(TakeAll(candidates, []), Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public void Past_capacity_the_oldest_of_the_lowest_ranked_is_dropped_and_a_rediscovered_peer_is_not_held_twice()
    {
        CustodyRankedCandidates candidates = new(Capacity);
        ulong[] wanted = [1];
        candidates.Add(Candidate("custodian", 1), wanted);
        candidates.Add(Candidate("oldest", 9), wanted);
        candidates.Add(Candidate("older", 9), wanted);
        candidates.Add(Candidate("older", 9), wanted);
        candidates.Add(Candidate("newer", 9), wanted);
        candidates.Add(Candidate("newest", 9), wanted);

        Assert.That(TakeAll(candidates, wanted), Is.EqualTo(new[] { "custodian", "older", "newer", "newest" }));
    }

    [TestCase(null, Eip7594DasConstants.CustodyRequirement, TestName = "A record without cgc custodies the CUSTODY_REQUIREMENT groups")]
    [TestCase(Eip7594DasConstants.NumberOfCustodyGroups, Eip7594DasConstants.NumberOfCustodyGroups, TestName = "A record with cgc custodies its advertised groups")]
    public void A_discovered_candidate_carries_the_custody_of_its_record(ulong? advertised, ulong expectedGroups)
    {
        byte[] digest = EnrForkId.Compute(BeaconChainSpec.Mainnet, 0).ForkDigest;
        NodeRecord record = new();
        record.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.8")));
        record.SetEntry(new TcpEntry(9000));
        record.SetEntry(new UdpEntry(9001));
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new Eth2Entry(EnrForkId.Compute(BeaconChainSpec.Mainnet, 0).Encode()));
        if (advertised is { } count)
        {
            record.SetEntry(new CustodyGroupCountEntry(count));
        }

        record.EnrSequence = 1;
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyA).Sign(record);

        Assert.That(BeaconDiscovery.TryCreateCandidate(NodeRecord.FromEnrString(record.ToString()), digest, null, out BeaconPeerCandidate? candidate), Is.True);
        Assert.That(Columns(candidate!.Custody), Is.EqualTo(Columns(PeerColumnCustody.ForNode(TestItem.PrivateKeyA.PublicKey.Hash, expectedGroups))));
    }

    private static BeaconPeerCandidate Candidate(string peerId, params ulong[] custodied) =>
        new($"/ip4/1.2.3.4/tcp/9000/p2p/{peerId}", peerId, [], 1, "enr:") { Custody = new PeerColumnCustody(custodied, isAdvertised: true) };

    private static List<string> TakeAll(CustodyRankedCandidates candidates, ulong[] wanted)
    {
        List<string> taken = [];
        while (candidates.TryTake(wanted, out BeaconPeerCandidate? candidate))
        {
            taken.Add(candidate.PeerId);
        }

        return taken;
    }

    private static ulong[] Columns(PeerColumnCustody custody) =>
        [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Where(custody.Custodies)];
}
