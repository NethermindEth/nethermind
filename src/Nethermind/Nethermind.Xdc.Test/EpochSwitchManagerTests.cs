// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class EpochSwitchManagerTests
{
    private static XdcBlockHeader CreateEpochSwitchBlock(IXdcReleaseSpec spec, ulong round, int blockNumber, Hash256 hash)
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Hash = hash;
        header.Number = (long)blockNumber + 1;
        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(hash, round - 1, header.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], (ulong)spec.Gap);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2((ulong)round + (ulong)spec.EpochLength, qc);
        header.ExtraConsensusData = extraFieldsV2;
        return header;
    }

    private static XdcBlockHeader CreateV2RegenesisBlock(IXdcReleaseSpec spec)
    {
        Address[] signers   = [TestItem.AddressA, TestItem.AddressB];

        var header = (XdcBlockHeader)Build.A.XdcBlockHeader()
            .WithNumber((long)spec.SwitchBlock)
            .WithExtraData(FillExtraDataForTests(signers)) //2 master nodes
            .WithParentHash(Keccak.EmptyTreeHash)
            .TestObject;

        header.PenaltiesAddress = [TestItem.AddressC];
        return header;
    }

    private static byte[] FillExtraDataForTests(Address[] nextEpochCandidates)
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
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(header, out ulong epochNumber);
        // Assert

        Assert.That(result, Is.True);
        Assert.That(switchBlock / epochLength, Is.EqualTo(epochNumber));
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
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(header, out ulong epochNumber);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(epochNumber, Is.EqualTo(0));
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnTrue_WhenProposedHeaderNumberIsSwitchBlock()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5ul;
        var round = 2ul;
        var gapNumber = 0ul;
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

        parentHeader.Number = (long)switchBlock - 1;
        parentHeader.Hash = headerHash;

        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;

        proposedHeader.Number = (long)switchBlock;
        proposedHeader.ParentHash = parentHeader.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(headerHash, round, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(round - 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;

        //proposedHeader.ExtraData.Returns();
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader, out ulong epochNumber);
        // Assert
        Assert.That(result, Is.True);
        Assert.That(switchBlock / epochLength, Is.EqualTo(epochNumber));
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnTrue_WhenParentRoundIsLessThanEpochStartRound()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 1ul;
        var round = 2ul;
        var gapNumber = 0ul;
        var headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Number = (long)switchBlock - 1;
        parentHeader.Hash = headerHash;
        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;
        proposedHeader.Number = (long)switchBlock + 1;
        proposedHeader.ParentHash = parentHeader.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(headerHash, round - 1, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(round, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader, out ulong epochNumber);
        // Assert
        Assert.That(result, Is.True);
        Assert.That((ulong)releaseSpec.SwitchEpoch + round / (ulong)releaseSpec.EpochLength, Is.EqualTo(epochNumber));
    }

    [Test]
    public void IsEpochSwitchAtBlock_ShouldReturnFalse_WhenParentRoundIsGreaterThanEpochStartRound()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5ul;
        var round = 2ul;
        var gapNumber = 0ul;
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
        parentHeader.Number = (long)switchBlock - 1;
        parentHeader.Hash = headerHash;
        XdcBlockHeader proposedHeader = Build.A.XdcBlockHeader()
            .TestObject;
        proposedHeader.Number = (long)switchBlock + 1;
        proposedHeader.ParentHash = parentHeader.Hash;

        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(headerHash, round, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(round - 1, qc);
        proposedHeader.ExtraConsensusData = extraFieldsV2;
        // Act
        bool result = _epochSwitchManager.IsEpochSwitchAtBlock(proposedHeader, out ulong epochNumber);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(epochNumber, Is.EqualTo(0));
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnTrue_WhenParentIssSwitchBlock()
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

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, parentHeader, out var epochNumber);
        // Assert
        Assert.That(result, Is.True);
        Assert.That((ulong)releaseSpec.SwitchEpoch + currRound / (ulong)releaseSpec.EpochLength, Is.EqualTo(epochNumber));
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

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(1, parentHeader, out var epochNumber);
        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsGreaterThanCurrentRound()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 5ul;
        var currRound = 2ul;
        var parentRound = 3ul;
        var gapNumber = 0ul;
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
        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(headerHash, parentRound, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(parentRound, qc);
        parentHeader.ExtraConsensusData = extraFieldsV2;

        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, parentHeader, out var epochNumber);
        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnTrue_WhenParentRoundIsLessThanEpochStartRound()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 2ul;
        var currRound = 5ul;
        var parentRound = 1ul;
        var gapNumber = 0ul;
        var headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = switchBlock,
            V2Configs = [new V2ConfigParams()],
            SwitchEpoch = 2
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Hash = headerHash;
        parentHeader.Number = (long)switchBlock - 1;
        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(headerHash, parentRound, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(parentRound, qc);
        parentHeader.ExtraConsensusData = extraFieldsV2;
        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, parentHeader, out var epochNumber);
        // Assert
        Assert.That(result, Is.True);
        Assert.That(((ulong)releaseSpec.SwitchEpoch + currRound) / (ulong)releaseSpec.EpochLength, Is.EqualTo(epochNumber));
        Assert.That(((ulong)releaseSpec.SwitchEpoch + currRound) / (ulong)releaseSpec.EpochLength, Is.GreaterThan(parentRound));
    }

    [Test]
    public void IsEpochSwitchAtRound_ShouldReturnFalse_WhenParentRoundIsGreaterToEpochStartRound()
    {
        // Arrange
        var switchBlock = 10ul;
        var epochLength = 4ul;
        var currRound = 3ul;
        var parentRound = 2ul;
        var gapNumber = 0ul;
        var headerHash = Keccak.Zero;
        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = 0,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Hash = headerHash;
        parentHeader.Number = (long)switchBlock - 1;
        QuorumCertificate qc = new QuorumCertificate(new BlockRoundInfo(headerHash, parentRound, parentHeader.Number), [TestItem.RandomSignatureA, TestItem.RandomSignatureB], gapNumber);
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2(parentRound, qc);
        parentHeader.ExtraConsensusData = extraFieldsV2;
        bool result = _epochSwitchManager.IsEpochSwitchAtRound(currRound, parentHeader, out var epochNumber);
        // Assert
        Assert.That(result, Is.False);
        Assert.That(((ulong)releaseSpec.SwitchEpoch + currRound) / (ulong)releaseSpec.EpochLength, Is.LessThan(parentRound));
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnNullIfHeaderIsNullAndParentIsNotInTree()
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

        _tree.FindHeader((long)blockNumber).Returns(header);
        var result = _epochSwitchManager.GetEpochSwitchInfo(header);
        result.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochSwitchIfBlockIsAtEpoch()
    {
        long blockNumber = 10;
        ulong epochLength = 5;
        ulong expectedEpochNumber = (ulong)blockNumber / epochLength;

        EpochSwitchInfo expected = new([TestItem.AddressA, TestItem.AddressB], [], [TestItem.AddressC], new BlockRoundInfo(TestItem.KeccakA, 0, blockNumber));

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = (ulong)blockNumber,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.Number = (long)blockNumber;
        header.ExtraData = FillExtraDataForTests([TestItem.AddressA, TestItem.AddressB]);
        header.Hash = TestItem.KeccakA;
        header.PenaltiesAddress = [TestItem.AddressC];

        _snapshotManager.GetSnapshot(TestItem.KeccakA).Returns(new Snapshot((long)blockNumber, TestItem.KeccakA, [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC]));
        _tree.FindHeader((long)blockNumber).Returns(header);
        _tree.FindHeader(header.Hash).Returns(header);

        var result = _epochSwitchManager.GetEpochSwitchInfo(header.Hash);
        Assert.That(result, Is.Not.Null);
        result.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void GetEpochSwitchInfo_ShouldReturnEpochNumbersIfParentBlockIsAtEpoch()
    {
        long blockNumber = 10;
        ulong epochLength = 5;
        ulong expectedEpochNumber = (ulong)blockNumber / epochLength;

        EpochSwitchInfo expected = new([TestItem.AddressA, TestItem.AddressB], [TestItem.AddressC], [TestItem.AddressD], new BlockRoundInfo(TestItem.KeccakA, 0, blockNumber));

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = (ulong)blockNumber-1,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // epoch switch block
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .TestObject;
        parentHeader.Number = (long)blockNumber;
        parentHeader.ExtraData = FillExtraDataForTests([TestItem.AddressA, TestItem.AddressB]);
        parentHeader.Hash = TestItem.KeccakA;
        parentHeader.PenaltiesAddress = [TestItem.AddressD];

        // not epoch switch block
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.ParentHash = parentHeader.Hash;
        header.Number = (long)blockNumber + 1;
        header.Hash = TestItem.KeccakB;

        _snapshotManager.GetSnapshot(TestItem.KeccakA).Returns(new Snapshot((long)blockNumber, TestItem.KeccakA, [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC]));
        _tree.FindHeader((long)blockNumber).Returns(parentHeader);
        _tree.FindHeader(header.ParentHash).Returns(parentHeader);
        _tree.FindHeader((long)blockNumber + 1).Returns(header);
        _tree.FindHeader(header.Hash).Returns(header);

        var result = _epochSwitchManager.GetEpochSwitchInfo(header.Hash);
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
    public void GetCurrentEpochNumbers_ShouldReturnEpochNumbersIfAtEpoch()
    {
        long switchRound = 10;
        long switchEpoch = 5;
        long blockNumber = 11;
        long epochLength = 5;
        long expectedEpochNumber = blockNumber / epochLength;

        XdcReleaseSpec releaseSpec = new()
        {
            EpochLength = (int)epochLength,
            SwitchBlock = (ulong)0,
            SwitchEpoch = (int)switchEpoch,
            V2Configs = [new V2ConfigParams()]
        };
        _config.GetSpecInternal(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        // epoch switch block
        XdcBlockHeader parentHeader = CreateEpochSwitchBlock(releaseSpec, (ulong)switchEpoch, (int)switchRound, TestItem.KeccakA);
        // not epoch switch block
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .TestObject;
        header.ParentHash = parentHeader.Hash;
        header.Number = blockNumber;
        header.Hash = TestItem.KeccakB;

        _snapshotManager.GetSnapshot(TestItem.KeccakA).Returns(new Snapshot((long)blockNumber, TestItem.KeccakA, [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC]));
        _tree.FindHeader((long)blockNumber).Returns(header);
        _tree.FindHeader(header.Hash).Returns(header);
        _tree.FindHeader((long)blockNumber - 1).Returns(parentHeader);
        _tree.FindHeader(parentHeader.Hash!).Returns(parentHeader);

        var result = _epochSwitchManager.GetCurrentEpochNumbers((ulong)header.Number);
        Assert.That(result, Is.Not.Null);
        Assert.That(result?.epochNumber, Is.EqualTo(switchEpoch));
        Assert.That(result?.currentCheckpointNumber, Is.EqualTo(blockNumber));
    }

    [Test]
    public void GetCurrentEpochNumbers_ShouldReturnNullIfBlockNumberIsNotInTree()
    {
        ulong blockNumber = 23;

        _tree.FindHeader((long)blockNumber).Returns((XdcBlockHeader?)null);

        var result = _epochSwitchManager.GetCurrentEpochNumbers(blockNumber);

        Assert.That(result, Is.Null);
    }


}
