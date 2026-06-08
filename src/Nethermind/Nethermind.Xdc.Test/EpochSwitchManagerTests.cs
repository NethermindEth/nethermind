// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Nethermind.Xdc.Test;

internal class EpochSwitchManagerTests
{
    private static readonly ImmutableArray<Address> SignerAddresses = [TestItem.AddressA, TestItem.AddressB];
    private static readonly ImmutableArray<Address> PenalizedAddresses = [TestItem.AddressC, TestItem.AddressD];
    private static readonly ImmutableArray<Address> StandbyAddresses = [TestItem.AddressE, TestItem.AddressF];
    private static readonly ImmutableArray<Signature> SignerSignatures = [TestItem.RandomSignatureA, TestItem.RandomSignatureB];

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
        _epochSwitchManager = new EpochSwitchManager(_config, _tree, _snapshotManager);
    }

    [TestCase(50UL, 100UL, 10UL, true)]
    [TestCase(53UL, 100UL, 10UL, false)]
    [TestCase(0UL, 100UL, 10UL, true)]
    public void IsEpochSwitchAtBlock_PreSwitchBlock(ulong blockNumber, ulong switchBlock, ulong epochLength, bool expected)
    {
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(blockNumber).TestObject;

        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(header);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnTrue_WhenBlockNumberIsSwitchBlock()
    {
        // Arrange
        ulong switchBlock = 10;
        ulong epochLength = 5;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };

        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;

        header.Number = switchBlock;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(header);
        // Assert

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalseWhenHeaderExtraDataFails()
    {
        // Arrange
        ulong switchBlock = 10;
        ulong epochLength = 5;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };

        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;

        header.Number = switchBlock + 1UL;
        header.ExtraData = Encoding.UTF8.GetBytes("InvalidExtraData");

        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(header);
        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnTrue_WhenProposedHeaderNumberIsSwitchBlock()
    {
        // Arrange
        ulong gapNumber = 0ul;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 101,
            V2Configs = [new V2ConfigParams()]
        };

        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = releaseSpec.SwitchBlock;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnTrue_WhenParentRoundIsLessThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 99);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.LessThan(extraFieldsV2.BlockRound));

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalse_WhenParentRoundIsGreaterThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 101);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound - 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.GreaterThan(extraFieldsV2.BlockRound));

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalse_WhenParentRoundIsEqualToEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.EqualTo(extraFieldsV2.BlockRound));

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnTrue_WhenParentIsSwitchBlock()
    {
        // Arrange
        ulong switchBlock = 10;
        ulong epochLength = 5;
        uint currRound = 2;
        Hash256 headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Hash = headerHash;
        parentHeader.Number = switchBlock;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, parentHeader);
        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenExtraConsensusDataIsNull()
    {
        // Arrange
        ulong switchBlock = 10;
        ulong epochLength = 5;
        Hash256 headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Hash = headerHash;
        parentHeader.Number = switchBlock - 1UL;
        parentHeader.ExtraConsensusData = null;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(1, parentHeader);
        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsGreaterThanBlockRound()
    {
        // Arrange
        ulong currRound = 42ul;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 101);

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, chainHead);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.GreaterThan(currRound));
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnTrue_WhenParentRoundIsLessThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };

        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // 99 is chosen so that parent round is less than epoch start round
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 99);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        ulong currentEpochNumber = releaseSpec.SwitchEpoch + extraFieldsV2.BlockRound / releaseSpec.EpochLength;
        ulong currentEpochStartRound = currentEpochNumber * releaseSpec.EpochLength;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(extraFieldsV2.BlockRound, chainHead);

        // Assert
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.EqualTo(extraFieldsV2.BlockRound - 1));
        Assert.That(currentEpochStartRound, Is.GreaterThan(chainHead.ExtraConsensusData!.BlockRound));
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.LessThan(extraFieldsV2.BlockRound));
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsEqualToEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };

        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // 101 is chosen that parent is at epoch and child is not at epoch
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        ulong currentEpochNumber = (releaseSpec.SwitchEpoch + extraFieldsV2.BlockRound) / releaseSpec.EpochLength;
        ulong currentEpochStartRound = currentEpochNumber * releaseSpec.EpochLength;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(extraFieldsV2.BlockRound, chainHead);

        // Assert
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.EqualTo(extraFieldsV2.BlockRound - 1));
        Assert.That(currentEpochStartRound, Is.EqualTo(chainHead.ExtraConsensusData!.BlockRound));
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.LessThan(extraFieldsV2.BlockRound));
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsGreaterThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };

        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // Create chain head at round 101 so that parent round is greater than epoch start round
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 101);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new(chainHead.ExtraConsensusData!.BlockRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        ulong currentEpochNumber = (releaseSpec.SwitchEpoch + extraFieldsV2.BlockRound) / releaseSpec.EpochLength;
        ulong currentEpochStartRound = currentEpochNumber * releaseSpec.EpochLength;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(extraFieldsV2.BlockRound, chainHead);

        // Assert
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.EqualTo(extraFieldsV2.BlockRound - 1));
        Assert.That(currentEpochStartRound, Is.LessThan(chainHead.ExtraConsensusData!.BlockRound));
        Assert.That(chainHead.ExtraConsensusData!.BlockRound, Is.LessThan(extraFieldsV2.BlockRound));
        Assert.That(result, Is.False);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnNullIfBlockHashIsNotInTree()
    {
        ulong switchBlock = 10;
        ulong epochLength = 4;
        Hash256 parentHash = Keccak.EmptyTreeHash;
        _tree.FindHeader(parentHash).Returns((XdcBlockHeader?)null);

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        EpochSwitchInfo? result = _epochSwitchManager.GetEpochSwitchInfo(parentHash);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochNumbersIfBlockIsAtEpoch_BlockNumber_Is_Zero()
    {
        Address[] signers = [TestItem.AddressA, TestItem.AddressB];
        ulong blockNumber = 0;
        Hash256 hash256 = Keccak.Zero;

        ulong epochLength = 5;
        ulong expectedEpochNumber = blockNumber / epochLength;

        EpochSwitchInfo expected = new(signers, [], [], new BlockRoundInfo(hash256, 0, blockNumber));

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = blockNumber,
            V2Configs = [new V2ConfigParams()],
            GenesisMasterNodes = signers,
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Hash = hash256;
        header.Number = blockNumber;
        header.ExtraData = FillExtraDataForTests([TestItem.AddressA, TestItem.AddressB]);

        _snapshotManager.GetSnapshotByBlockNumber(blockNumber, Arg.Any<IXdcReleaseSpec>()).Returns(new Snapshot(header.Number, header.Hash!, signers));

        _tree.FindHeader(blockNumber).Returns(header);
        EpochSwitchInfo? result = _epochSwitchManager.GetEpochSwitchInfo(header);
        Assert.That(result, Is.EqualTo(expected).UsingXdcComparer());
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochSwitchIfBlockIsAtEpoch()
    {

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);
        XdcBlockHeader parentHeader = (XdcBlockHeader)_tree.FindHeader(chainHead.ParentHash!)!;

        EpochSwitchInfo expected = new(SignerAddresses.ToArray(), StandbyAddresses.ToArray(), PenalizedAddresses.ToArray(), new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.BlockRound, chainHead.Number));
        expected.EpochSwitchParentBlockInfo = new(parentHeader.Hash!, parentHeader.ExtraConsensusData!.BlockRound, parentHeader.Number);

        EpochSwitchInfo? result = _epochSwitchManager.GetEpochSwitchInfo(chainHead.Hash!);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(expected).UsingXdcComparer());
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochNumbersIfParentBlockIsAtEpoch()
    {
        ulong blockNumber = 100;
        ulong epochLength = 5;
        ulong expectedEpochNumber = blockNumber / epochLength;


        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // 101 is chosen that parent is at epoch and child is not at epoch
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, (int)blockNumber + 1);

        XdcBlockHeader parentHeader = (XdcBlockHeader)_tree.FindHeader(blockNumber)!;
        EpochSwitchInfo expected = new(
            parentHeader.ValidatorsAddress!.Value.ToArray(),
            StandbyAddresses.ToArray(),
            parentHeader.PenaltiesAddress!.Value.ToArray(),
            new BlockRoundInfo(parentHeader.Hash!, parentHeader.ExtraConsensusData!.BlockRound, blockNumber));

        expected.EpochSwitchParentBlockInfo = new(parentHeader.ParentHash!, parentHeader.ExtraConsensusData.BlockRound - (ulong)1, parentHeader.Number - 1);

        EpochSwitchInfo? result = _epochSwitchManager.GetEpochSwitchInfo(chainHead.Hash!);
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(expected).UsingXdcComparer());
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnNullIfBlockIsAtEpochAndSnapshotIsNull()
    {
        ulong blockNumber = 10;
        ulong epochLength = 5;
        ulong expectedEpochNumber = blockNumber / epochLength;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = blockNumber,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Number = blockNumber;
        header.ExtraData = FillExtraDataForTests([TestItem.AddressA, TestItem.AddressB]);
        header.Hash = TestItem.KeccakA;

        _snapshotManager.GetSnapshotByBlockNumber(blockNumber, Arg.Any<IXdcReleaseSpec>()).Returns((Snapshot)null!);

        _tree.FindHeader(blockNumber).Returns(header);
        _tree.FindHeader(header.Hash).Returns(header);

        EpochSwitchInfo? result = _epochSwitchManager.GetEpochSwitchInfo(header.Hash);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetBlockByEpochNumber_ShouldReturnNullIfNoBlockFound()
    {
        // Arrange
        ulong epochNumber = 10ul;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = 5UL,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // Act
        BlockRoundInfo? result = _epochSwitchManager.GetBlockByEpochNumber(epochNumber);
        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetBlockByEpochNumber_ShouldReturnBlockIfFound()
    {
        // Arrange
        ulong epochLength = 5ul;
        ulong epochNumber = 7ul;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        Block headBlock = new(chainHead);
        _tree.Head.Returns(headBlock);

        ulong expectedBlockNumber = epochNumber * epochLength;

        BlockRoundInfo? result = _epochSwitchManager.GetBlockByEpochNumber(epochNumber);

        Assert.That(result?.BlockNumber, Is.EqualTo(expectedBlockNumber));
    }

    [Test]
    public void GetTimeoutCertificateEpochInfo_ShouldReturnEpochSwitchInfoForEpochContainingTcRound()
    {
        ulong epochLength = 5;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = epochLength,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 20);

        Block headBlock = new(chainHead);
        _tree.Head.Returns(headBlock);

        // TC round 12 is within epoch that started at round 10
        TimeoutCertificate timeoutCertificate = new(12, [], 0);
        EpochSwitchInfo? result = _epochSwitchManager.GetTimeoutCertificateEpochInfo(timeoutCertificate);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EpochSwitchBlockInfo.Round, Is.EqualTo(10));
    }

    private XdcBlockHeader GetChainOfBlocks(IBlockTree tree, ISnapshotManager snapManager, IXdcReleaseSpec spec, int length, int startRound = 0)
    {
        int i = startRound;
        XdcBlockHeader block = CreateV2RegenesisBlock(spec);
        do
        {
            if (i != startRound)
            {
                block = GenNormalBlock(spec, block!);
            }

            if ((block.ExtraConsensusData?.BlockRound ?? 0ul) % (ulong)spec.EpochLength == 0)
            {
                snapManager.GetSnapshotByBlockNumber(block.Number, Arg.Any<IXdcReleaseSpec>()).Returns(new Snapshot(block.Number, block.Hash!, [.. StandbyAddresses, .. SignerAddresses]));
            }

            tree.FindHeader(block.Hash!).Returns(block);
            tree.FindHeader(block.Number).Returns(block);

        } while (i++ < length);

        return block;
    }

    private XdcBlockHeader GenNormalBlock(IXdcReleaseSpec spec, XdcBlockHeader? parent)
    {
        ulong newRound = 0;
        Hash256? parentHash = null;
        ulong prevRound = 0;
        ulong blockNumber = 0;
        if (parent is not null)
        {
            newRound = 1 + (parent.ExtraConsensusData?.BlockRound ?? 0);
            blockNumber = 1 + parent.Number;
            prevRound = parent.ExtraConsensusData?.BlockRound ?? 0;
            parentHash = parent.Hash;

        }
        Hash256 newBlockHash = Keccak.Compute(BitConverter.GetBytes(blockNumber).PadLeft(32));


        QuorumCertificate qc = new(new BlockRoundInfo(parent?.Hash ?? Keccak.Zero, prevRound, parent?.Number ?? 0), SignerSignatures.ToArray(), (ulong)spec.Gap);
        ExtraFieldsV2 extraFieldsV2 = new((ulong)newRound, qc);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Hash = newBlockHash;
        header.Number = blockNumber;
        header.ExtraConsensusData = extraFieldsV2;
        header.ParentHash = parentHash;

        header.ValidatorsAddress = SignerAddresses;
        header.PenaltiesAddress = PenalizedAddresses;

        return header;
    }

    private XdcBlockHeader CreateV2RegenesisBlock(IXdcReleaseSpec spec)
    {
        Address[] signers = [TestItem.AddressA, TestItem.AddressB];

        XdcBlockHeader header = (XdcBlockHeader)Build.A.XdcBlockHeader()
            .WithNumber(spec.SwitchBlock)
            .WithExtraData(FillExtraDataForTests(signers)) //2 master nodes
            .WithParentHash(Keccak.EmptyTreeHash)
            .TestObject;

        header.PenaltiesAddress = [TestItem.AddressC];
        return header;
    }

    private byte[] FillExtraDataForTests(Address[] nextEpochCandidates)
    {
        int length = Address.Size * nextEpochCandidates?.Length ?? 0;
        byte[] extraData = new byte[XdcConstants.ExtraVanity + length + XdcConstants.ExtraSeal];

        for (int i = 0; i < nextEpochCandidates!.Length; i++)
        {
            nextEpochCandidates[i].Bytes.CopyTo(extraData.AsSpan(XdcConstants.ExtraVanity + i * Address.Size, Address.Size));
        }

        return extraData;
    }
}
