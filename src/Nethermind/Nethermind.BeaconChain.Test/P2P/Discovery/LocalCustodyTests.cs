// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// Subnet derivation for a known (fixed) node id. Mainnet's <c>NUMBER_OF_COLUMNS ==
/// NUMBER_OF_CUSTODY_GROUPS == 128</c> coincidence (see <c>CustodyGroupsTests</c>) makes
/// group == column == subnet, so the expected subnets below are independently recomputed straight
/// from <see cref="CustodyGroups.GetCustodyGroups"/> rather than copied from <see cref="LocalCustody"/>'s
/// own output.
/// </summary>
public class LocalCustodyTests
{
    private static readonly Hash256 KnownNodeId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Test]
    public void Derives_the_requested_count_of_distinct_sorted_subnets_for_a_known_node_id()
    {
        ulong[] expectedGroups = CustodyGroups.GetCustodyGroups(KnownNodeId, Eip7594DasConstants.CustodyRequirement);

        LocalCustody custody = new(KnownNodeId, Eip7594DasConstants.CustodyRequirement);

        Assert.Multiple(() =>
        {
            Assert.That(custody.CustodyGroupCount, Is.EqualTo(Eip7594DasConstants.CustodyRequirement));
            Assert.That(custody.CustodyGroups, Is.EqualTo(expectedGroups));
            // Group == column == subnet under the mainnet preset coincidence, so the subnet set
            // must equal the group set exactly (same count, same values, same order).
            Assert.That(custody.Subnets, Is.EqualTo(expectedGroups));
            Assert.That(custody.Subnets, Is.Ordered);
            Assert.That(custody.Subnets.Distinct().Count(), Is.EqualTo(custody.Subnets.Count));
            Assert.That(custody.Subnets, Is.All.LessThan(Eip7594DasConstants.DataColumnSidecarSubnetCount));
        });
    }

    [Test]
    public void Is_deterministic_for_the_same_node_id_and_count()
    {
        LocalCustody first = new(KnownNodeId, Eip7594DasConstants.SamplesPerSlot);
        LocalCustody second = new(KnownNodeId, Eip7594DasConstants.SamplesPerSlot);

        Assert.That(second.Subnets, Is.EqualTo(first.Subnets));
    }

    [Test]
    public void Different_node_ids_derive_different_subnets()
    {
        Hash256 otherNodeId = new(Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray());

        LocalCustody first = new(KnownNodeId, Eip7594DasConstants.CustodyRequirement);
        LocalCustody second = new(otherNodeId, Eip7594DasConstants.CustodyRequirement);

        Assert.That(second.Subnets, Is.Not.EqualTo(first.Subnets));
    }
}
