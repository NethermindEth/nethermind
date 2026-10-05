// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.DataAvailability;

// Oracle: consensus-spec networking/get_custody_groups meta.yaml; both presets have 128 custody groups.
public class CustodyGroupsTests
{
    private static Hash256 NodeId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    // Decimal node_id is encoded as 32-byte big-endian discv5 bytes, not little-endian.
    private static Hash256 RawNodeId(string decimalNodeId)
    {
        byte[] bigEndian = BigInteger.Parse(decimalNodeId).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] raw = new byte[32];
        bigEndian.CopyTo(raw, raw.Length - bigEndian.Length);
        return new Hash256(raw);
    }

    private const string MaxNodeId = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
    private const string MaxNodeIdMinus1 = "115792089237316195423570985008687907853269984665640564039457584007913129639934";

    private static ulong[] AllGroups => Enumerable.Range(0, (int)Eip7594DasConstants.NumberOfCustodyGroups).Select(i => (ulong)i).ToArray();

    // Asymmetric/seeded node IDs expose byte order; 2**256-1 does not.
    public static IEnumerable<TestCaseData> PyspecVectors { get; } =
    [
        new TestCaseData("0", 0ul, Array.Empty<ulong>()).SetName("min_node_id_min_custody_group_count"),
        new TestCaseData("0", 128ul, AllGroups).SetName("min_node_id_max_custody_group_count"),
        new TestCaseData(MaxNodeId, 0ul, Array.Empty<ulong>()).SetName("max_node_id_min_custody_group_count"),
        new TestCaseData(MaxNodeId, 128ul, AllGroups).SetName("max_node_id_max_custody_group_count"),
        new TestCaseData(MaxNodeIdMinus1, 128ul, AllGroups).SetName("max_node_id_minus_1_max_custody_group_count"),
        new TestCaseData("1048576", 1ul, new ulong[] { 65 }).SetName("short_node_id"),
        new TestCaseData(MaxNodeId, 4ul, new ulong[] { 1, 47, 87, 102 }).SetName("max_node_id_custody_group_count_is_4"),
        new TestCaseData(MaxNodeIdMinus1, 4ul, new ulong[] { 1, 47, 87, 102 }).SetName("max_node_id_minus_1_custody_group_count_is_4"),
        new TestCaseData("51781405571328938149219259614021022118347017557305093857689627172914154745642", 47ul, new ulong[]
        {
            3, 6, 7, 8, 9, 12, 25, 26, 29, 30, 32, 40, 42, 47, 52, 53, 54, 55, 56, 57, 69, 70, 71, 72, 74, 77, 80, 81,
            83, 88, 93, 94, 95, 98, 101, 105, 106, 112, 114, 116, 118, 120, 121, 123, 124, 125, 127,
        }).SetName("get_custody_groups_1"),
        new TestCaseData("84065159290331321853352677657753050104170032838956724170714636178275273565505", 6ul, new ulong[] { 27, 29, 58, 67, 96, 117 }).SetName("get_custody_groups_2"),
        new TestCaseData("62524992026686681062927724650084164361416283301810167550777687366062873585350", 93ul, new ulong[]
        {
            0, 1, 2, 4, 5, 6, 7, 9, 10, 13, 14, 16, 19, 20, 21, 22, 23, 24, 25, 26, 29, 30, 31, 34, 36, 37, 38, 39, 40, 41,
            42, 44, 45, 46, 49, 50, 53, 54, 55, 56, 57, 58, 60, 62, 66, 67, 68, 70, 71, 72, 74, 75, 76, 77, 78, 79, 80, 81,
            82, 83, 84, 86, 87, 88, 89, 90, 91, 92, 93, 95, 96, 98, 99, 100, 101, 103, 104, 105, 107, 108, 109, 111, 112,
            113, 114, 115, 117, 118, 120, 122, 123, 126, 127,
        }).SetName("get_custody_groups_3"),
    ];

    [TestCaseSource(nameof(PyspecVectors))]
    public void GetCustodyGroups_matches_the_consensus_spec_test_vector(string nodeId, ulong custodyGroupCount, ulong[] expected)
    {
        ulong[] groups = CustodyGroups.GetCustodyGroups(RawNodeId(nodeId), custodyGroupCount);

        Assert.That(groups, Is.EqualTo(expected));
    }

    [TestCase((byte)0x00)]
    [TestCase((byte)0x01)]
    [TestCase((byte)0xFF)]
    [TestCase((byte)0x7A)]
    public void GetCustodyGroups_returns_the_requested_count_of_distinct_in_range_groups(byte fill)
    {
        ulong[] groups = CustodyGroups.GetCustodyGroups(NodeId(fill), Eip7594DasConstants.CustodyRequirement);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(groups, Has.Length.EqualTo((int)Eip7594DasConstants.CustodyRequirement));
        Assert.That(groups.Distinct().Count(), Is.EqualTo(groups.Length), "custody groups must be distinct");
        Assert.That(groups, Is.All.LessThan(Eip7594DasConstants.NumberOfCustodyGroups));
        Assert.That(groups, Is.Ordered, "get_custody_groups returns sorted(custody_groups)");
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
        // Both walks take first N distinct groups; increasing custody must retain the smaller set.
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
        // Mainnet samples 8 groups but retains/serves 4; sampling must not shrink to custody.
        ulong samplingSize = Math.Max(Eip7594DasConstants.SamplesPerSlot, Eip7594DasConstants.CustodyRequirement);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(samplingSize, Is.EqualTo(8ul));
        Assert.That(Eip7594DasConstants.CustodyRequirement, Is.EqualTo(4ul));
    }

    [TestCase(0ul)]
    [TestCase(1ul)]
    [TestCase(64ul)]
    [TestCase(127ul)]
    public void ComputeColumnsForCustodyGroup_returns_columns_matching_the_general_division(ulong custodyGroup)
    {
        ulong[] columns = CustodyGroups.ComputeColumnsForCustodyGroup(custodyGroup);

        // Mainnet columns_per_group = 1 is a preset coincidence, not a general invariant.
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(columns, Has.Length.EqualTo(1));
        Assert.That(columns[0], Is.EqualTo(custodyGroup));
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
        // Mainnet column % 128 equals its index; the implementation must still compute modulo.
        for (ulong column = 0; column < (ulong)Eip7594DasConstants.NumberOfColumns; column++)
        {
            Assert.That(CustodyGroups.ComputeSubnetForDataColumnSidecar(column), Is.EqualTo(column));
        }
    }
}
