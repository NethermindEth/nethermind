// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using MathNet.Numerics.Distributions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class EpochSwitchManagerTests
{
    private static ImmutableArray<Address> SignerAddresses = [TestItem.AddressA, TestItem.AddressB];
    private static ImmutableArray<Address> PenalizedAddresses = [TestItem.AddressC, TestItem.AddressD];
    private static ImmutableArray<Address> StandbyAddresses = [TestItem.AddressE, TestItem.AddressF];
    private static ImmutableArray<Signature> SignerSignatures = [TestItem.RandomSignatureA, TestItem.RandomSignatureB];
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

            if ((block.ExtraConsensusData?.CurrentRound ?? 0ul) % (ulong)spec.EpochLength == 0)
            {
                snapManager.GetSnapshot(block.Hash!).Returns(new Snapshot(block.Number, block.Hash!, [.. StandbyAddresses, .. SignerAddresses]));
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
        long blockNumber = 0;
        if (parent is not null)
        {
            newRound = 1 + (parent.ExtraConsensusData?.CurrentRound ?? 0);
            blockNumber = 1 + parent.Number;
            prevRound = parent.ExtraConsensusData?.CurrentRound ?? 0;
            parentHash = parent.Hash;

        }
        Hash256 newBlockHash = Keccak.Compute(BitConverter.GetBytes(blockNumber).PadLeft(32));


        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(parent?.Hash ?? Keccak.Zero, prevRound, parent?.Number ?? 0), SignerSignatures.ToArray(), (ulong)spec.Gap);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2((ulong)newRound, qc);

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

        var header = (XdcBlockHeader)Build.A.XdcBlockHeader()
            .WithNumber((long)spec.SwitchBlock)
            .WithExtraData(FillExtraDataForTests(signers)) //2 master nodes
            .WithParentHash(Keccak.EmptyTreeHash)
            .TestObject;

        header.PenaltiesAddress = [TestItem.AddressC];
        return header;
    }

    private byte[] FillExtraDataForTests(Address[] nextEpochCandidates)
    {
        var length = Address.Size * nextEpochCandidates?.Length ?? 0;
        var extraData = new byte[XdcConstants.ExtraVanity + length + XdcConstants.ExtraSeal];

        for (int i = 0; i < nextEpochCandidates!.Length; i++)
        {
            Array.Copy(nextEpochCandidates[i].Bytes, 0, extraData, XdcConstants.ExtraVanity + i * Address.Size, Address.Size);
        }

        return extraData;
    }

    private IEpochSwitchManager _epochSwitchManager;
    private IBlockTree _tree = NSubstitute.Substitute.For<IBlockTree>();
    private ISpecProvider _config = NSubstitute.Substitute.For<ISpecProvider>();
    private ISnapshotManager _snapshotManager = NSubstitute.Substitute.For<ISnapshotManager>();

    [SetUp]
    public void Setup()
    {
        _epochSwitchManager = new EpochSwitchManager(_config, _tree, _snapshotManager);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnTrue_WhenBlockNumberIsSwitchBlock()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5ul;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };

        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;

        header.Number = (long)switchBlock;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(header);
        // Assert

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalseWhenHeaderExtraDataFails()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };

        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;

        header.Number = (long)switchBlock + 1;
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
        var gapNumber = 0ul;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 101,
            V2Configs = [new V2ConfigParams()]
        };

        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)releaseSpec.SwitchBlock;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound + 1, qc);
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
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 99);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.LessThan(extraFieldsV2.CurrentRound));

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalse_WhenParentRoundIsGreaterThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 101);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound - 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.GreaterThan(extraFieldsV2.CurrentRound));

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalse_WhenParentRoundIsEqualToEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader);
        // Assert
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.EqualTo(extraFieldsV2.CurrentRound));

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnTrue_WhenParentIsSwitchBlock()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5ul;
        var currRound = 2ul;
        var headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Hash = headerHash;
        parentHeader.Number = (long)switchBlock;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, parentHeader);
        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenExtraConsensusDataIsNull()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5ul;
        var headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Hash = headerHash;
        parentHeader.Number = (long)switchBlock - 1;
        parentHeader.ExtraConsensusData = null;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(1, parentHeader);
        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsGreaterThanCurrentRound()
    {
        // Arrange
        var currRound = 42ul;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 101);

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, chainHead);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.GreaterThan(currRound));
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnTrue_WhenParentRoundIsLessThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };

        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // 99 is chosen so that parent round is less than epoch start round
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 99);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        ulong currentEpochNumber = (ulong)releaseSpec.SwitchEpoch + extraFieldsV2.CurrentRound / (ulong)releaseSpec.EpochLength;
        ulong currentEpochStartRound = currentEpochNumber * (ulong)releaseSpec.EpochLength;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(extraFieldsV2.CurrentRound, chainHead);

        // Assert
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.EqualTo(extraFieldsV2.CurrentRound - 1));
        Assert.That(currentEpochStartRound, Is.GreaterThan(chainHead.ExtraConsensusData!.CurrentRound));
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.LessThan(extraFieldsV2.CurrentRound));
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsEqualToEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };

        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // 101 is chosen that parent is at epoch and child is not at epoch
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        ulong currentEpochNumber = ((ulong)releaseSpec.SwitchEpoch + extraFieldsV2.CurrentRound) / (ulong)releaseSpec.EpochLength;
        ulong currentEpochStartRound = currentEpochNumber * (ulong)releaseSpec.EpochLength;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(extraFieldsV2.CurrentRound, chainHead);

        // Assert
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.EqualTo(extraFieldsV2.CurrentRound - 1));
        Assert.That(currentEpochStartRound, Is.EqualTo(chainHead.ExtraConsensusData!.CurrentRound));
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.LessThan(extraFieldsV2.CurrentRound));
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsGreaterThanEpochStartRound()
    {
        // Arrange
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };

        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // Create chain head at round 101 so that parent round is greater than epoch start round
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 101);

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)chainHead.Number + 1;
        proposedHeader.ParentHash = chainHead.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number), SignerSignatures.ToArray(), 1);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(chainHead.ExtraConsensusData!.CurrentRound + 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        ulong currentEpochNumber = ((ulong)releaseSpec.SwitchEpoch + extraFieldsV2.CurrentRound) / (ulong)releaseSpec.EpochLength;
        ulong currentEpochStartRound = currentEpochNumber * (ulong)releaseSpec.EpochLength;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(extraFieldsV2.CurrentRound, chainHead);

        // Assert
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.EqualTo(extraFieldsV2.CurrentRound - 1));
        Assert.That(currentEpochStartRound, Is.LessThan(chainHead.ExtraConsensusData!.CurrentRound));
        Assert.That(chainHead.ExtraConsensusData!.CurrentRound, Is.LessThan(extraFieldsV2.CurrentRound));
        Assert.That(result, Is.False);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnNullIfBlockHashIsNotInTree()
    {
        var switchBlock = 10ul;
        var epochLength = 4ul;
        var parentHash = Keccak.EmptyTreeHash;
        _tree.FindHeader(parentHash).Returns((XdcBlockHeader?)null);

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        var result = _epochSwitchManager.GetEpochSwitchInfo(parentHash);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochNumbersIfBlockIsAtEpoch_BlockNumber_Is_Zero()
    {
        ulong blockNumber = 0;
        Hash256 hash256 = Keccak.Zero;

        ulong epochLength = 5;
        ulong expectedEpochNumber = blockNumber / epochLength;

        Address[] signers = [TestItem.AddressA, TestItem.AddressB];
        EpochSwitchInfo expected = new(signers, [], [], new BlockRoundInfo(hash256, 0, (long)blockNumber));

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = blockNumber,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Hash = hash256;
        header.Number = (long)blockNumber;
        header.ExtraData = FillExtraDataForTests([TestItem.AddressA, TestItem.AddressB]);

        _snapshotManager.GetSnapshot(hash256).Returns(new Snapshot(header.Number, header.Hash!, signers));

        _tree.FindHeader((long)blockNumber).Returns(header);
        var result = _epochSwitchManager.GetEpochSwitchInfo(header);
        result.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochSwitchIfBlockIsAtEpoch()
    {

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = (ulong)0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);
        var parentHeader = (XdcBlockHeader)_tree.FindHeader(chainHead.ParentHash!)!;

        EpochSwitchInfo expected = new(SignerAddresses.ToArray(), StandbyAddresses.ToArray(), PenalizedAddresses.ToArray(), new BlockRoundInfo(chainHead.Hash!, chainHead.ExtraConsensusData!.CurrentRound, chainHead.Number));
        expected.EpochSwitchParentBlockInfo = new(parentHeader.Hash!, parentHeader.ExtraConsensusData!.CurrentRound, parentHeader.Number);

        var result = _epochSwitchManager.GetEpochSwitchInfo(chainHead.Hash!);

        Assert.That(result, Is.Not.Null);
        result.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochNumbersIfParentBlockIsAtEpoch()
    {
        ulong blockNumber = 100;
        ulong epochLength = 5;
        ulong expectedEpochNumber = (ulong)blockNumber / epochLength;


        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // 101 is chosen that parent is at epoch and child is not at epoch
        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, (int)blockNumber + 1);

        var parentHeader = (XdcBlockHeader)_tree.FindHeader((long)blockNumber)!;
        EpochSwitchInfo expected = new(
            parentHeader.ValidatorsAddress!.Value.ToArray(),
            StandbyAddresses.ToArray(),
            parentHeader.PenaltiesAddress!.Value.ToArray(),
            new BlockRoundInfo(parentHeader.Hash!, parentHeader.ExtraConsensusData!.CurrentRound, (long)blockNumber));

        expected.EpochSwitchParentBlockInfo = new(parentHeader.ParentHash!, parentHeader.ExtraConsensusData.CurrentRound - (ulong)1, parentHeader.Number - 1);

        var result = _epochSwitchManager.GetEpochSwitchInfo(chainHead.Hash!);
        Assert.That(result, Is.Not.Null);
        result.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnNullIfBlockIsAtEpochAndSnapshotIsNull()
    {
        ulong blockNumber = 10;
        ulong epochLength = 5;
        ulong expectedEpochNumber = blockNumber / epochLength;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = blockNumber,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Number = (long)blockNumber;
        header.ExtraData = FillExtraDataForTests([TestItem.AddressA, TestItem.AddressB]);
        header.Hash = TestItem.KeccakA;

        _snapshotManager.GetSnapshot(TestItem.KeccakA).Returns((Snapshot)null!);

        _tree.FindHeader((long)blockNumber).Returns(header);
        _tree.FindHeader(header.Hash).Returns(header);

        var result = _epochSwitchManager.GetEpochSwitchInfo(header.Hash);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetBlockByEpochNumber_ShouldReturnNullIfNoBlockFound()
    {
        // Arrange
        var epochNumber = 10ul;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)5,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        _tree.FindHeader(Arg.Any<long>()).Returns((XdcBlockHeader?)null);
        // Act
        var result = _epochSwitchManager.GetBlockByEpochNumber(epochNumber);
        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetBlockByEpochNumber_ShouldReturnBlockIfFound()
    {
        // Arrange
        var epochLength = 5ul;
        var epochNumber = 7ul;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader chainHead = GetChainOfBlocks(_tree, _snapshotManager, releaseSpec, 100);

        var headBlock = new Block(chainHead);
        _tree.Head.Returns(headBlock);

        int expectedBlockNumber = (int)(epochNumber * epochLength);

        var result = _epochSwitchManager.GetBlockByEpochNumber(epochNumber);

        Assert.That(result?.BlockNumber, Is.EqualTo(expectedBlockNumber));
    }

}
