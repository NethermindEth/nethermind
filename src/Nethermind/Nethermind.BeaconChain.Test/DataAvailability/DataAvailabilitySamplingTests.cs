// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class DataAvailabilitySamplingTests
{
    private static Hash256 NodeId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    [TestCase(0ul, 8ul)]
    [TestCase(4ul, 8ul)]
    [TestCase(8ul, 8ul)]
    [TestCase(16ul, 16ul)]
    [TestCase(128ul, 128ul)]
    public void GetSamplingSize_is_the_max_of_samples_per_slot_and_custody_group_count(ulong custodyGroupCount, ulong expected) =>
        Assert.That(DataAvailabilitySampling.GetSamplingSize(custodyGroupCount), Is.EqualTo(expected));

    [Test]
    public void GetColumnsToSample_returns_sampling_size_many_distinct_in_range_columns()
    {
        ulong[] columns = DataAvailabilitySampling.GetColumnsToSample(NodeId(0x42), Eip7594DasConstants.CustodyRequirement);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        // Mainnet: columns-per-group == 1, so the column count equals sampling_size (max(8, 4) = 8).
        Assert.That(columns, Has.Length.EqualTo(8));
        Assert.That(columns.Distinct().Count(), Is.EqualTo(columns.Length));
        Assert.That(columns, Is.All.LessThan(Eip7594DasConstants.NumberOfColumns));
        Assert.That(columns, Is.Ordered);
    }

    // Independent pyspec oracle: raw node-id bytes 00..1f read big-endian; 4 custody groups and 8 sampling groups.
    [Test]
    public void GetColumnsToSample_is_the_spec_sample_and_a_superset_of_the_nodes_own_custody_columns()
    {
        // The spec's own invariant: "the custody groups to custody ... are then in particular a
        // subset of those to sample." A node must never sample fewer columns than it custodies.
        Hash256 nodeId = new(Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray());
        ulong[] expectedCustodyColumns = [57, 84, 105, 113];
        ulong[] expectedSample = [40, 57, 61, 84, 102, 105, 113, 120];

        ulong[] custodyColumns = [.. CustodyGroups.GetCustodyGroups(nodeId, Eip7594DasConstants.CustodyRequirement)
            .SelectMany(CustodyGroups.ComputeColumnsForCustodyGroup)];
        ulong[] sampledColumns = DataAvailabilitySampling.GetColumnsToSample(nodeId, Eip7594DasConstants.CustodyRequirement);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(custodyColumns, Is.EqualTo(expectedCustodyColumns));
        Assert.That(sampledColumns, Is.EqualTo(expectedSample));
        Assert.That(expectedCustodyColumns, Is.SubsetOf(expectedSample));
    }

    [Test]
    public void GetColumnsToSample_is_deterministic_for_the_same_node_id()
    {
        Hash256 nodeId = NodeId(0x11);

        ulong[] first = DataAvailabilitySampling.GetColumnsToSample(nodeId, Eip7594DasConstants.CustodyRequirement);
        ulong[] second = DataAvailabilitySampling.GetColumnsToSample(nodeId, Eip7594DasConstants.CustodyRequirement);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void GetColumnsToSample_when_custody_exceeds_samples_per_slot_samples_at_least_the_full_custody()
    {
        Hash256 nodeId = NodeId(0x55);
        ulong custodyGroupCount = Eip7594DasConstants.SamplesPerSlot + 10;

        ulong[] columns = DataAvailabilitySampling.GetColumnsToSample(nodeId, custodyGroupCount);

        Assert.That(columns, Has.Length.EqualTo((int)custodyGroupCount));
    }

    [TestCase(true, TestName = "IsSampleAvailable_true_only_when_every_sampled_column_is_held")]
    [TestCase(false, TestName = "IsSampleAvailable_false_when_one_sampled_column_is_missing")]
    public void IsSampleAvailable_requires_every_sampled_column(bool lastColumnHeld)
    {
        ulong[] columnsToSample = [1, 5, 9];
        bool[] held = [false, true, false, false, false, true, false, false, false, lastColumnHeld];

        Assert.That(DataAvailabilitySampling.IsSampleAvailable(columnsToSample, c => held[c]), Is.EqualTo(lastColumnHeld));
    }

    [Test]
    public void IsSampleAvailable_does_not_short_circuit_into_vacuous_truth_on_an_empty_sample_set() =>
        Assert.That(DataAvailabilitySampling.IsSampleAvailable([], _ => throw new InvalidOperationException("must not be called")), Is.False);

    [Test]
    public void IsSampleAvailable_rejects_null_arguments() =>
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentNullException>(() => DataAvailabilitySampling.IsSampleAvailable(null!, _ => true));
            Assert.Throws<ArgumentNullException>(() => DataAvailabilitySampling.IsSampleAvailable([1], null!));
        });
}
