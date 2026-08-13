// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class SubnetEpochSwitchManagerTests
{
    private IEpochSwitchManager _epochSwitchManager;
    private IBlockTree _tree;
    private ISpecProvider _config;
    private ISnapshotManager _snapshotManager;

    [SetUp]
    public void Setup()
    {
        _tree = Substitute.For<IBlockTree>();
        _config = Substitute.For<ISpecProvider>();
        _snapshotManager = Substitute.For<ISnapshotManager>();
        _epochSwitchManager = new SubnetEpochSwitchManager(_config, _tree, _snapshotManager);
    }

    [TestCase(20UL, 10UL, true)]
    [TestCase(5UL, 10UL, false)]
    public void IsEpochSwitchAtBlock_BlockNumberBased(ulong blockNumber, ulong epochLength, bool expected)
    {
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
        builder.WithNumber(blockNumber);
        XdcSubnetBlockHeader header = builder.TestObject;

        Assert.That(_epochSwitchManager.IsEpochSwitchAtBlock(header), Is.EqualTo(expected));
    }

    [TestCase(9UL, 10UL, true)]   // parent.Number + 1 = 10, 10 % 10 == 0
    [TestCase(5UL, 10UL, false)]  // parent.Number + 1 = 6
    public void IsEpochSwitchAtRound_DerivedFromParentBlockNumber(ulong parentNumber, ulong epochLength, bool expected)
    {
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
        builder.WithNumber(parentNumber);
        XdcSubnetBlockHeader parent = builder.TestObject;

        // currentRound is deliberately varied — subnet ignores it
        Assert.That(_epochSwitchManager.IsEpochSwitchAtRound(0, parent), Is.EqualTo(expected));
        Assert.That(_epochSwitchManager.IsEpochSwitchAtRound(999, parent), Is.EqualTo(expected));
        Assert.That(_epochSwitchManager.IsEpochSwitchAtRound(ulong.MaxValue, parent), Is.EqualTo(expected));
    }

    [Test]
    public void GetEpochSwitchInfo_PenaltiesFromSubnetSnapshot_NotFromHeader()
    {
        Address[] snapshotPenalties = [TestItem.AddressC];
        Address[] headerPenalties = [TestItem.AddressD]; // deliberately different
        Address[] masterNodes = [TestItem.AddressA, TestItem.AddressB];

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 10,
            Gap = 5,
            SwitchBlock = 0,
            GenesisMasterNodes = masterNodes,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // Block 0 is an epoch switch (0 % 10 == 0), so no parent-walk needed
        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
        builder.WithNumber(0);
        builder.WithHash(TestItem.KeccakA);
        builder.WithPenalties(headerPenalties);
        XdcSubnetBlockHeader header = builder.TestObject;

        SubnetSnapshot subnetSnapshot = new(header.Number, header.Hash!, [.. masterNodes], snapshotPenalties);
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, Arg.Any<IXdcReleaseSpec>()).Returns(subnetSnapshot);

        EpochSwitchInfo? result = _epochSwitchManager.GetEpochSwitchInfo(header);

        Assert.That(result, Is.Not.Null);
        // Penalties must come from SubnetSnapshot, NOT from header
        Assert.That(result!.Penalties, Is.EquivalentTo(snapshotPenalties));
        Assert.That(result.Penalties, Is.Not.EquivalentTo(headerPenalties));
    }

    [Test]
    public void GetEpochSwitchInfo_NonSubnetSnapshot_Throws()
    {
        Address[] masterNodes = [TestItem.AddressA, TestItem.AddressB];

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 10,
            Gap = 5,
            SwitchBlock = 0,
            GenesisMasterNodes = masterNodes,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
        builder.WithNumber(0);
        builder.WithHash(TestItem.KeccakA);
        XdcSubnetBlockHeader header = builder.TestObject;

        Snapshot baseSnapshot = new(header.Number, header.Hash!, masterNodes);
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, Arg.Any<IXdcReleaseSpec>()).Returns(baseSnapshot);

        Assert.That(() => _epochSwitchManager.GetEpochSwitchInfo(header), Throws.InstanceOf<ArgumentException>());
    }

    private const ulong EpochLength = 10;

    /// <summary>
    /// Registers an epoch switch block with the substituted tree and snapshot manager.
    /// </summary>
    /// <param name="withQuorumCertificate">
    /// When <see langword="false"/>, models the switch block: no consensus data, so no parent block info.
    /// </param>
    private XdcSubnetBlockHeader SetupEpochSwitchBlock(ulong number, bool withQuorumCertificate = true)
    {
        Address[] masterNodes = [TestItem.AddressA, TestItem.AddressB];

        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
        builder.WithNumber(number);
        builder.WithHash(Keccak.Compute($"epoch-switch-{number}"));
        builder.WithValidators(masterNodes);
        if (withQuorumCertificate)
        {
            builder.WithExtraConsensusData(new ExtraFieldsV2(number, Build.A.QuorumCertificate().TestObject));
        }
        XdcSubnetBlockHeader header = builder.TestObject;

        _tree.FindHeader(number).Returns(header);
        _snapshotManager.GetSnapshotByBlockNumber(number, Arg.Any<IXdcReleaseSpec>())
            .Returns(new SubnetSnapshot(number, header.Hash!, [.. masterNodes], []));

        return header;
    }

    private void SetupSpec(ulong switchBlock = 0) =>
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(new XdcReleaseSpec
        {
            EpochLength = EpochLength,
            Gap = 5,
            SwitchBlock = switchBlock,
            GenesisMasterNodes = [TestItem.AddressA, TestItem.AddressB],
            V2Configs = [new V2ConfigParams()]
        });

    private XdcSubnetBlockHeader PlainHeader(ulong number)
    {
        XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
        builder.WithNumber(number);
        return builder.TestObject;
    }

    [TestCase(5UL, 35UL, new ulong[] { 10, 20, 30 }, TestName = "spans partial epochs at both ends")]
    [TestCase(10UL, 30UL, new ulong[] { 10, 20, 30 }, TestName = "includes both exact boundaries")]
    [TestCase(11UL, 29UL, new ulong[] { 20 }, TestName = "excludes boundaries outside the range")]
    [TestCase(11UL, 19UL, new ulong[] { }, TestName = "no epoch switch in range")]
    [TestCase(20UL, 20UL, new ulong[] { }, TestName = "empty range")]
    [TestCase(30UL, 20UL, new ulong[] { }, TestName = "end before start")]
    public void GetEpochSwitchInfoBetween_ReturnsEpochSwitchesInRange(ulong start, ulong end, ulong[] expected)
    {
        SetupSpec();
        for (ulong number = EpochLength; number <= 40; number += EpochLength)
        {
            SetupEpochSwitchBlock(number);
        }

        EpochSwitchInfo[]? result = _epochSwitchManager.GetEpochSwitchInfoBetween(PlainHeader(start), PlainHeader(end));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Select(static i => i.EpochSwitchBlockInfo.BlockNumber), Is.EqualTo(expected).AsCollection);
    }

    [Test]
    public void GetEpochSwitchInfoBetween_ExcludesSwitchBlockWithoutQuorumCertificate()
    {
        SetupSpec();
        SetupEpochSwitchBlock(0, withQuorumCertificate: false);
        SetupEpochSwitchBlock(10);
        SetupEpochSwitchBlock(20);

        EpochSwitchInfo[]? result = _epochSwitchManager.GetEpochSwitchInfoBetween(PlainHeader(0), PlainHeader(20));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Select(static i => i.EpochSwitchBlockInfo.BlockNumber), Is.EqualTo(new ulong[] { 10, 20 }).AsCollection);
    }

    [Test]
    public void GetEpochSwitchInfoBetween_MissingHeader_ReturnsNull()
    {
        SetupSpec();
        SetupEpochSwitchBlock(10);
        _tree.FindHeader(20UL).Returns((BlockHeader?)null);

        Assert.That(_epochSwitchManager.GetEpochSwitchInfoBetween(PlainHeader(5), PlainHeader(25)), Is.Null);
    }

    [Test]
    public void GetEpochSwitchInfoBetween_MissingSnapshot_ReturnsNull()
    {
        SetupSpec();
        XdcSubnetBlockHeader header = SetupEpochSwitchBlock(10);
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, Arg.Any<IXdcReleaseSpec>()).Returns((Snapshot?)null);

        Assert.That(_epochSwitchManager.GetEpochSwitchInfoBetween(PlainHeader(5), PlainHeader(15)), Is.Null);
    }
}
