// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
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

    [TestCase(20L, 10, true)]
    [TestCase(5L, 10, false)]
    public void IsEpochSwitchAtBlock_BlockNumberBased(long blockNumber, int epochLength, bool expected)
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

    [TestCase(9L, 10, true)]   // parent.Number + 1 = 10, 10 % 10 == 0
    [TestCase(5L, 10, false)]  // parent.Number + 1 = 6
    public void IsEpochSwitchAtRound_DerivedFromParentBlockNumber(long parentNumber, int epochLength, bool expected)
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

}
