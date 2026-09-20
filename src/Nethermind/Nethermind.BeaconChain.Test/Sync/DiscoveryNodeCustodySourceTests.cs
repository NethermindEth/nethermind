// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// The importer's custody must be derived from the very node id discovery advertises, or the columns
/// this node demands of a block and the columns it tells peers it custodies drift apart silently.
/// </summary>
public class DiscoveryNodeCustodySourceTests
{
    [Test]
    public void NodeIdOf_recovers_the_discv5_node_id_the_local_custody_was_derived_from()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
        record.EnrSequence = 1;
        new NodeRecordSigner(new Ecdsa(), key).Sign(record);
        // Parse back from the string form so the entry takes the same path as a wire record.
        NodeRecord parsed = NodeRecord.FromEnrString(record.ToString());

        Assert.That(DiscoveryNodeCustodySource.NodeIdOf(parsed), Is.EqualTo(key.PublicKey.Hash),
            "BeaconDiscovery seeds LocalCustody with nodeKey.PublicKey.Hash; the ENR must round-trip to that exact value");
    }

    [Test]
    public void Custody_columns_demanded_by_the_importer_are_the_columns_advertised_to_peers()
    {
        Hash256 nodeId = TestItem.PrivateKeyB.PublicKey.Hash;
        LocalCustody advertised = new(nodeId, Eip7594DasConstants.CustodyRequirement);
        NodeColumnCustody demanded = new(nodeId, advertised.CustodyGroupCount);

        Assert.Multiple(() =>
        {
            Assert.That(demanded.CustodyColumns, Is.EqualTo(advertised.CustodyGroups.SelectMany(CustodyGroups.ComputeColumnsForCustodyGroup).Order()));
            Assert.That(demanded.CustodyColumns, Is.SubsetOf(demanded.SampledColumns), "the per-slot sample always covers the node's own custody");
            Assert.That(demanded.SampledColumns, Has.Count.EqualTo((int)Eip7594DasConstants.SamplesPerSlot));
        });
    }

    [Test]
    public void Without_discovery_there_is_no_identity() =>
        Assert.That(new DiscoveryNodeCustodySource(null).Current, Is.Null);
}
