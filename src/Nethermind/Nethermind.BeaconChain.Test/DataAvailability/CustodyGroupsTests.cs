// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// No official <c>get_custody_groups</c> test vectors were located for this session (das-core.md
/// gives only the algorithm, not fixture data), so these test the invariants the spec's own
/// assertions require - right count, all distinct, all in range, deterministic - rather than a
/// specific expected set. See 'unresolved' in the delivering task report.
/// </summary>
public class CustodyGroupsTests
{
    private static Hash256 NodeId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    [TestCase((byte)0x00)]
    [TestCase((byte)0x01)]
    [TestCase((byte)0xFF)]
    [TestCase((byte)0x7A)]
    public void GetCustodyGroups_returns_the_requested_count_of_distinct_in_range_groups(byte fill)
    {
        ulong[] groups = CustodyGroups.GetCustodyGroups(NodeId(fill), Eip7594DasConstants.CustodyRequirement);

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Length.EqualTo((int)Eip7594DasConstants.CustodyRequirement));
            Assert.That(groups.Distinct().Count(), Is.EqualTo(groups.Length), "custody groups must be distinct");
            Assert.That(groups, Is.All.LessThan(Eip7594DasConstants.NumberOfCustodyGroups));
            Assert.That(groups, Is.Ordered, "get_custody_groups returns sorted(custody_groups)");
        });
    }

    [Test]
    public void GetCustodyGroups_is_deterministic_for_the_same_node_id()
    {
        Hash256 nodeId = NodeId(0x42);

        ulong[] first = CustodyGroups.GetCustodyGroups(nodeId, Eip7594DasConstants.SamplesPerSlot);
        ulong[] second = CustodyGroups.GetCustodyGroups(nodeId, Eip7594DasConstants.SamplesPerSlot);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void GetCustodyGroups_different_node_ids_are_not_trivially_identical()
    {
        ulong[] a = CustodyGroups.GetCustodyGroups(NodeId(0x11), Eip7594DasConstants.CustodyRequirement);
        ulong[] b = CustodyGroups.GetCustodyGroups(NodeId(0x99), Eip7594DasConstants.CustodyRequirement);

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void GetCustodyGroups_smaller_count_is_a_subset_of_larger_count_for_the_same_node()
    {
        // Guaranteed by construction: both walks start at node_id and take the first N distinct
        // hits in the same order, so the count=4 result must be contained in the count=8 result.
        Hash256 nodeId = NodeId(0x37);

        ulong[] small = CustodyGroups.GetCustodyGroups(nodeId, Eip7594DasConstants.CustodyRequirement);
        ulong[] large = CustodyGroups.GetCustodyGroups(nodeId, Eip7594DasConstants.SamplesPerSlot);

        Assert.That(small, Is.SubsetOf(large));
    }

    [Test]
    public void GetCustodyGroups_full_count_returns_every_group_in_order()
    {
        ulong[] groups = CustodyGroups.GetCustodyGroups(NodeId(0x00), Eip7594DasConstants.NumberOfCustodyGroups);

        Assert.That(groups, Is.EqualTo(Enumerable.Range(0, (int)Eip7594DasConstants.NumberOfCustodyGroups).Select(i => (ulong)i)));
    }

    [Test]
    public void GetCustodyGroups_zero_count_returns_empty() =>
        Assert.That(CustodyGroups.GetCustodyGroups(NodeId(0x00), 0), Is.Empty);

    [Test]
    public void GetCustodyGroups_rejects_a_count_above_the_group_total() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CustodyGroups.GetCustodyGroups(NodeId(0x00), Eip7594DasConstants.NumberOfCustodyGroups + 1));

    [Test]
    public void Non_attesting_node_sampling_size_is_eight_and_custody_is_four()
    {
        // max(SAMPLES_PER_SLOT=8, CUSTODY_REQUIREMENT=4) = 8: the node samples 8 groups per slot but
        // only retains/serves 4 long-term. See 'deviations' for why this count, not just its value.
        ulong samplingSize = Math.Max(Eip7594DasConstants.SamplesPerSlot, Eip7594DasConstants.CustodyRequirement);

        Assert.Multiple(() =>
        {
            Assert.That(samplingSize, Is.EqualTo(8ul));
            Assert.That(Eip7594DasConstants.CustodyRequirement, Is.EqualTo(4ul));
        });
    }

    [TestCase(0ul)]
    [TestCase(1ul)]
    [TestCase(64ul)]
    [TestCase(127ul)]
    public void ComputeColumnsForCustodyGroup_returns_columns_matching_the_general_division(ulong custodyGroup)
    {
        ulong[] columns = CustodyGroups.ComputeColumnsForCustodyGroup(custodyGroup);

        // Mainnet has NUMBER_OF_COLUMNS == NUMBER_OF_CUSTODY_GROUPS == 128, so columns_per_group is 1
        // and custody_group == its one column - a preset coincidence the implementation must not assume.
        Assert.Multiple(() =>
        {
            Assert.That(columns, Has.Length.EqualTo(1));
            Assert.That(columns[0], Is.EqualTo(custodyGroup));
        });
    }

    [Test]
    public void ComputeColumnsForCustodyGroup_rejects_an_out_of_range_group() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CustodyGroups.ComputeColumnsForCustodyGroup(Eip7594DasConstants.NumberOfCustodyGroups));

    [Test]
    public void ComputeSubnetForDataColumnSidecar_is_total_and_in_range_for_every_column()
    {
        for (ulong column = 0; column < (ulong)Eip7594DasConstants.NumberOfColumns; column++)
        {
            ulong subnet = CustodyGroups.ComputeSubnetForDataColumnSidecar(column);
            Assert.That(subnet, Is.LessThan(Eip7594DasConstants.DataColumnSidecarSubnetCount));
        }
    }

    [Test]
    public void ComputeSubnetForDataColumnSidecar_matches_the_mainnet_identity_coincidence()
    {
        // NUMBER_OF_COLUMNS == DATA_COLUMN_SIDECAR_SUBNET_COUNT == 128 on mainnet, so column_index % 128
        // is the identity function - true today, but the implementation computes the modulo, not this.
        for (ulong column = 0; column < (ulong)Eip7594DasConstants.NumberOfColumns; column++)
        {
            Assert.That(CustodyGroups.ComputeSubnetForDataColumnSidecar(column), Is.EqualTo(column));
        }
    }
}
