// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class EpochSwitchManagerTests
{
    private IEpochSwitchManager _epochSwitchManager;
    private IBlockTree _tree = NSubstitute.Substitute.For<IBlockTree>();
    private IXdcConfig _config = NSubstitute.Substitute.For<IXdcConfig>();
    private ISnapshotManager _snapshotManager = NSubstitute.Substitute.For<ISnapshotManager>();

    [SetUp]
    public void Setup()
    {
        _epochSwitchManager = new EpochSwitchManager(_config, _tree, _snapshotManager);
    }

    [Test]
    public void IsEpochSwitch_ShouldReturnTrue_WhenBlockNumberIsSwitchBlock()
    {
        // Arrange
        var switchBlock = 10ul;
        var epoch = 5ul;
        _config.Epoch.Returns(epoch);
        _config.SwitchBlock.Returns((long)switchBlock);
        var header = NSubstitute.Substitute.For<XdcBlockHeader>();
        header.Number.Returns((long)switchBlock);
        // Act
        bool result = _epochSwitchManager.IsEpochSwitch(header, out ulong epochNumber);
        // Assert

        Assert.That(result, Is.True);
        Assert.That(switchBlock / epoch, Is.EqualTo(epochNumber));
    }

    [Test]
    public void IsEpochSwitch_ShouldReturnFalseWhenHeaderExtraDataFails()
    {
        // Arrange
        var switchBlock = 10ul;
        var epoch = 5ul;
        _config.Epoch.Returns(epoch);
        _config.SwitchBlock.Returns((long)switchBlock);
        var header = NSubstitute.Substitute.For<XdcBlockHeader>();
        header.Number.Returns((long)(switchBlock + 1));
        header.ExtraData.Returns(Encoding.UTF8.GetBytes("InvalidExtraData"));

        // Act
        bool result = _epochSwitchManager.IsEpochSwitch(header, out ulong epochNumber);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(epochNumber, Is.EqualTo(0));
    }

    [Test]
    public void IsEpochSwitch_ShouldReturnTrue_WhenProposedHeaderNumberIsSwitchBlock()
    {
        // Arrange
        var switchBlock = 10ul;
        var epoch = 5ul;
        var round = 1ul;
        var gapNumber = 0ul;
        var headerHash = Keccak.Zero;

        _config.Epoch.Returns(epoch);
        _config.SwitchBlock.Returns((long)switchBlock);

        var parentHeader = NSubstitute.Substitute.For<XdcBlockHeader>();
        parentHeader.Number.Returns((long)(switchBlock - 1));
        parentHeader.Hash.Returns(headerHash);

        var proposedHeader = NSubstitute.Substitute.For<XdcBlockHeader>();
        proposedHeader.Number.Returns((long)switchBlock);
        proposedHeader.ParentHash.Returns(parentHeader.Hash);

        ExtraConsensusDataDecoder extraConsensusDataDecoder = new();
        QuorumCert qc = new QuorumCert(new BlockInfo(headerHash, round, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);

        //proposedHeader.ExtraData.Returns();
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader, out ulong epochNumber);
        // Assert
        Assert.That(result, Is.True);
        Assert.That(switchBlock / epoch, Is.EqualTo(epochNumber));
    }

    [Test]
    public void IsEpochSwitch_ShouldReturnTrue_WhenParentRoundIsLessThanEpochStartRound()
    {

    }

    [Test]
    public void IsEpochSwitch_ShouldReturnFalse_WhenParentRoundIsGreaterThanEpochStartRound()
    {

    }
}
