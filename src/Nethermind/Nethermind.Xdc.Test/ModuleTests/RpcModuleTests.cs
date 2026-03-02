// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

[TestFixture, NonParallelizable]
public class RpcModuleTests
{
    private IBlockTree _blockTree;
    private ISnapshotManager _snapshotManager;
    private ISpecProvider _specProvider;
    private IQuorumCertificateManager _quorumCertificateManager;
    private IEpochSwitchManager _epochSwitchManager;
    private IVotesManager _votesManager;
    private ITimeoutCertificateManager _timeoutCertificateManager;
    private ISyncInfoManager _syncInfoManager;
    private XdcRpcModule _rpcModule;


    private EpochSwitchInfo[] GenerateEpochSwitchInfos(long begin, long end, int switchEpoch, int epochLength)
    {
        var epochSwitchInfos = new List<EpochSwitchInfo>();
        for (long blockNum = begin; blockNum <= end; blockNum += epochLength)
        {
            ulong epochNumber = (ulong)(blockNum / epochLength);
            if (epochNumber >= (ulong)switchEpoch)
            {
                epochSwitchInfos.Add(new EpochSwitchInfo(
                    Array.Empty<Address>(),
                    Array.Empty<Address>(),
                    Array.Empty<Address>(),
                    new BlockRoundInfo(TestItem.KeccakA, 100, blockNum)));
            }
        }
        return epochSwitchInfos.ToArray();
    }

    private XdcReleaseSpec CreateDummyXdcReleaseSpec(
        int? switchEpoch = null,
        int? epochLength = null,
        long? switchBlock = null,
        int? maxMasternodes = null,
        double? certThreshold = null,
        int? timeoutPeriod = null,
        int? minePeriod = null)
    {
        var spec = new XdcReleaseSpec
        {
            // Epoch configuration
            SwitchEpoch = switchEpoch ?? 0,
            EpochLength = epochLength ?? 900,
            SwitchBlock = switchBlock ?? 0,
            Gap = 5,

            // V2 Configuration
            MaxMasternodes = maxMasternodes ?? 108,
            MaxProtectorNodes = 0,  // Not used in current implementation
            MaxObserverNodes = 0,   // Not used in current implementation
            SwitchRound = 0,

            // Timing parameters
            MinePeriod = minePeriod ?? 2,              // 2 seconds per block
            TimeoutSyncThreshold = 3,                   // Send sync info after 3 timeouts
            TimeoutPeriod = timeoutPeriod ?? 30000,    // 30 seconds timeout

            // Consensus thresholds
            CertThreshold = certThreshold ?? 0.667,     // 2/3 majority for certificates

            // Reward configuration (in Wei)
            Reward = 5000,
            MasternodeReward = 5000,
            ProtectorReward = 0,
            ObserverReward = 0,

            // Penalty configuration
            MinimumMinerBlockPerEpoch = 1,
            LimitPenaltyEpoch = 3,
            MinimumSigningTx = 1,

            // Smart contract addresses (using zero addresses for tests)
            GenesisMasterNodes = Array.Empty<Address>(),
            BlockSignerContract = Address.Zero,
            RandomizeSMCBinary = Address.Zero,
            XDCXLendingFinalizedTradeAddressBinary = Address.Zero,
            XDCXLendingAddressBinary = Address.Zero,
            XDCXAddressBinary = Address.Zero,
            TradingStateAddressBinary = Address.Zero,
            FoundationWallet = Address.Zero,
            MasternodeVotingContract = Address.Zero,
            RelayerRegistrationSMC = Address.Zero,
            TRC21IssuerSMC = Address.Zero,

            // Feature flags
            IsBlackListingEnabled = false,
            IsTIP2019 = true,
            IsTIPXDCXMiner = false,

            // Other settings
            MergeSignRange = 15,
            BlackListedAddresses = new HashSet<Address>(),

            // V2 configuration parameters
            V2Configs = new List<V2ConfigParams>
        {
            new V2ConfigParams
            {
                SwitchRound = 0,
                MaxMasternodes = maxMasternodes ?? 108,
                CertThreshold = certThreshold ?? 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = timeoutPeriod ?? 30000,
                MinePeriod = minePeriod ?? 2
            }
        }
        };

        return spec;
    }


    [SetUp]
    public void Setup()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _snapshotManager = Substitute.For<ISnapshotManager>();
        _specProvider = Substitute.For<ISpecProvider>();
        _quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        _epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        _votesManager = Substitute.For<IVotesManager>();
        _timeoutCertificateManager = Substitute.For<ITimeoutCertificateManager>();
        _syncInfoManager = Substitute.For<ISyncInfoManager>();

        _rpcModule = new XdcRpcModule(
            _blockTree,
            _snapshotManager,
            _specProvider,
            _quorumCertificateManager,
            _epochSwitchManager,
            _votesManager,
            _timeoutCertificateManager,
            _syncInfoManager);
    }

    #region CalculateBlockInfoByV1EpochNum Tests

    [Test]
    public void CalculateBlockInfoByV1EpochNum_ShouldThrowNotSupportedException()
    {
        // Act & Assert
        Assert.Throws<NotSupportedException>(() => _rpcModule.CalculateBlockInfoByV1EpochNum(1));
    }

    #endregion

    #region GetBlockInfoByV2EpochNum Tests

    [Test]
    public void GetBlockInfoByV2EpochNum_ShouldReturnSuccess_WhenEpochExists()
    {
        // Arrange
        ulong epochNumber = 5;
        Hash256 expectedHash = TestItem.KeccakA;
        ulong expectedRound = 100;
        long expectedBlockNumber = 500;

        BlockRoundInfo blockRoundInfo = new(expectedHash, expectedRound, expectedBlockNumber);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns(blockRoundInfo);

        BlockRoundInfo nextBlockRoundInfo = new(TestItem.KeccakB, 120, 600);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber + 1).Returns(nextBlockRoundInfo);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.GetBlockInfoByV2EpochNum(epochNumber);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().NotBeNull();
        result.Data!.EpochBlockHash.Should().Be(expectedHash);
        result.Data.EpochRound.Should().Be((UInt256)expectedRound);
        result.Data.EpochFirstBlockNumber.Should().Be((UInt256)expectedBlockNumber);
        result.Data.EpochLastBlockNumber.Should().Be((UInt256)(nextBlockRoundInfo.BlockNumber - 1));
        result.Data.EpochConsensusVersion.Should().Be("v2");
    }

    [Test]
    public void GetBlockInfoByV2EpochNum_ShouldReturnSuccess_WhenNextEpochDoesNotExist()
    {
        // Arrange
        ulong epochNumber = 5;
        Hash256 expectedHash = TestItem.KeccakA;
        ulong expectedRound = 100;
        long expectedBlockNumber = 500;

        BlockRoundInfo blockRoundInfo = new(expectedHash, expectedRound, expectedBlockNumber);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns(blockRoundInfo);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber + 1).Returns((BlockRoundInfo?)null);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.GetBlockInfoByV2EpochNum(epochNumber);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().NotBeNull();
        result.Data!.EpochLastBlockNumber.Should().BeNull();
    }

    [Test]
    public void GetBlockInfoByV2EpochNum_ShouldReturnFail_WhenEpochNotFound()
    {
        // Arrange
        ulong epochNumber = 999;
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns((BlockRoundInfo?)null);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.GetBlockInfoByV2EpochNum(epochNumber);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    #endregion

    #region GetBlockInfoByEpochNum Tests

    [Test]
    public void GetBlockInfoByEpochNum_ShouldCallV1Method_WhenEpochNumberBelowSwitchEpoch()
    {
        // Arrange
        ulong epochNumber = 3;
        long headNumber = 100;
        int switchEpoch = 5;

        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = headNumber;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        XdcReleaseSpec spec = new() { SwitchEpoch = switchEpoch };
        _specProvider.GetXdcSpec(Arg.Any<XdcBlockHeader>(), Arg.Any<ulong>()).Returns(spec);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => _rpcModule.GetBlockInfoByEpochNum(epochNumber));
    }

    [Test]
    public void GetBlockInfoByEpochNum_ShouldCallV2Method_WhenEpochNumberAboveOrEqualSwitchEpoch()
    {
        // Arrange
        ulong epochNumber = 10;
        long headNumber = 100;
        int switchEpoch = 5;

        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = headNumber;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        XdcReleaseSpec spec = new() { SwitchEpoch = switchEpoch };
        _specProvider.GetXdcSpec(headNumber).Returns(spec);

        BlockRoundInfo blockRoundInfo = new(TestItem.KeccakA, 100, 500);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns(blockRoundInfo);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.GetBlockInfoByEpochNum(epochNumber);

        // Assert
        result.Result.Should().Be(Result.Success);
        _epochSwitchManager.Received(1).GetBlockByEpochNumber(epochNumber);
    }

    #endregion

    #region GetEpochNumbersBetween Tests

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnSuccess_WhenValidRange()
    {
        // Arrange
        long begin = 100;
        long end = 200;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        EpochSwitchInfo[] epochSwitchInfos = new[]
        {
            new EpochSwitchInfo(Array.Empty<Address>(), Array.Empty<Address>(), Array.Empty<Address>(), new BlockRoundInfo(TestItem.KeccakA, 10, 100)),
            new EpochSwitchInfo(Array.Empty<Address>(), Array.Empty<Address>(), Array.Empty<Address>(), new BlockRoundInfo(TestItem.KeccakB, 20, 150))
        };

        _epochSwitchManager.GetEpochSwitchInfoBetween(beginHeader, endHeader).Returns(epochSwitchInfos);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.GetEpochNumbersBetween(begin, end);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().HaveCount(2);
        result.Data.Should().Equal(100UL, 150UL);
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenBeginHeaderNotFound()
    {
        // Arrange
        long begin = 100;
        long end = 200;

        _blockTree.FindHeader(begin).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.GetEpochNumbersBetween(begin, end);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenEndHeaderNotFound()
    {
        // Arrange
        long begin = 100;
        long end = 200;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.GetEpochNumbersBetween(begin, end);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenBeginGreaterThanEnd()
    {
        // Arrange
        long begin = 200;
        long end = 100;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.GetEpochNumbersBetween(begin, end);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenRangeExceedsLimit()
    {
        // Arrange
        long begin = 100;
        long end = 50_101;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.GetEpochNumbersBetween(begin, end);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenHeadersAreNotXdcHeaders()
    {
        // Arrange
        long begin = 100;
        long end = 200;

        BlockHeader beginHeader = Build.A.BlockHeader.TestObject;
        beginHeader.Number = begin;

        BlockHeader endHeader = Build.A.BlockHeader.TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.GetEpochNumbersBetween(begin, end);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    #endregion

    #region GetLatestPoolStatus Tests

    [Test]
    public void GetLatestPoolStatus_ShouldReturnSuccess_WhenValidState()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;

        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        Address[] masternodes = new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC };
        EpochSwitchInfo epochSwitchInfo = new(
            masternodes,
            Array.Empty<Address>(),
            Array.Empty<Address>(),
            new BlockRoundInfo(TestItem.KeccakA, 10, 100));

        _epochSwitchManager.GetEpochSwitchInfo(header).Returns(epochSwitchInfo);

        var receivedVotes = new Dictionary<(ulong Round, Hash256 Hash), ArrayPoolList<Nethermind.Xdc.Types.Vote>>();
        var voteList = new ArrayPoolList<Nethermind.Xdc.Types.Vote>(2);
        Nethermind.Xdc.Types.Vote vote1 = new(new BlockRoundInfo(TestItem.KeccakA, 10, 100), 0) { Signer = TestItem.AddressA };
        Nethermind.Xdc.Types.Vote vote2 = new(new BlockRoundInfo(TestItem.KeccakA, 10, 100), 0) { Signer = TestItem.AddressB };
        voteList.Add(vote1);
        voteList.Add(vote2);
        receivedVotes[(10UL, TestItem.KeccakA)] = voteList;

        _votesManager.GetReceivedVotes().Returns(receivedVotes);
        _timeoutCertificateManager.GetReceivedTimeouts().Returns(new Dictionary<(ulong, Hash256), ArrayPoolList<Timeout>>());
        _syncInfoManager.GetReceivedSyncInfos().Returns(new Dictionary<(ulong, Hash256), ArrayPoolList<SyncInfo>>());

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.GetLatestPoolStatus();

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Vote.Should().NotBeNull();
        result.Data.Timeout.Should().NotBeNull();
        result.Data.SyncInfo.Should().NotBeNull();
    }

    [Test]
    public void GetLatestPoolStatus_ShouldReturnFail_WhenNoHead()
    {
        // Arrange
        _blockTree.Head.Returns((Block?)null);

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.GetLatestPoolStatus();

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetLatestPoolStatus_ShouldReturnFail_WhenHeaderIsNotXdcHeader()
    {
        // Arrange
        BlockHeader header = Build.A.BlockHeader.TestObject;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.GetLatestPoolStatus();

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetLatestPoolStatus_ShouldReturnFail_WhenEpochSwitchInfoIsNull()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _epochSwitchManager.GetEpochSwitchInfo(header).Returns((EpochSwitchInfo?)null);

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.GetLatestPoolStatus();

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    #endregion

    #region GetMasternodesByNumber Tests

    [Test]
    public void GetMasternodesByNumber_ShouldReturnSuccess_WithLatestBlockParameter()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        QuorumCertificate qc = new(new BlockRoundInfo(TestItem.KeccakA, 50, 100), null, 50);
        header.ExtraConsensusData = new ExtraFieldsV2(50, qc);

        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        XdcReleaseSpec spec = new() { SwitchEpoch = 5, EpochLength = 10 };
        _specProvider.GetXdcSpec(header).Returns(spec);

        Address[] masternodes = new[] { TestItem.AddressA, TestItem.AddressB };
        Address[] penalties = new[] { TestItem.AddressC };
        Address[] standbynodes = new[] { TestItem.AddressD };

        EpochSwitchInfo epochSwitchInfo = new(
            masternodes,
            penalties,
            standbynodes,
            new BlockRoundInfo(TestItem.KeccakA, 50, 100));

        _epochSwitchManager.GetEpochSwitchInfo(header).Returns(epochSwitchInfo);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(BlockParameter.Latest);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Masternodes.Should().BeEquivalentTo(masternodes);
        result.Data.Penalty.Should().BeEquivalentTo(penalties);
        result.Data.Standbynodes.Should().BeEquivalentTo(standbynodes);
        result.Data.Number.Should().Be(100);
        result.Data.Round.Should().Be((UInt256)50);
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnSuccess_WithFinalizedBlockParameter()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        BlockRoundInfo proposedBlock = new(header.Hash!, 50, 100);
        QuorumCertificate qc = new(proposedBlock, null, 50);
        header.ExtraConsensusData = new ExtraFieldsV2(50, qc);

        _quorumCertificateManager.HighestKnownCertificate.Returns(qc);
        _blockTree.FindHeader(header.Hash!).Returns(header);

        XdcReleaseSpec spec = new() { SwitchEpoch = 5, EpochLength = 10 };
        _specProvider.GetXdcSpec(header).Returns(spec);

        Address[] masternodes = new[] { TestItem.AddressA };
        EpochSwitchInfo epochSwitchInfo = new(
            masternodes,
            Array.Empty<Address>(),
            Array.Empty<Address>(),
            new BlockRoundInfo(TestItem.KeccakA, 50, 100));

        _epochSwitchManager.GetEpochSwitchInfo(header).Returns(epochSwitchInfo);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(BlockParameter.Finalized);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().NotBeNull();
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenFinalizedBlockNotFound()
    {
        // Arrange
        _quorumCertificateManager.HighestKnownCertificate.Returns((QuorumCertificate?)null);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(BlockParameter.Finalized);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenInvalidBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(-1);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenHeaderNotFound()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        _blockTree.FindHeader(100).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenHeaderIsNotXdcHeader()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        BlockHeader header = Build.A.BlockHeader.TestObject;
        header.Number = 100;
        _blockTree.FindHeader(100).Returns(header);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenNoConsensusData()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        header.ExtraConsensusData = null;

        _blockTree.FindHeader(100).Returns(header);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.GetMasternodesByNumber(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    #endregion

    #region GetSigners Tests

    [Test]
    public void GetSigners_ShouldReturnSuccess_WithLatestBlockParameter()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;

        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        XdcReleaseSpec spec = new();
        _specProvider.GetXdcSpec(100).Returns(spec);

        Address[] expectedSigners = new[] { TestItem.AddressA, TestItem.AddressB };
        Nethermind.Xdc.Types.Snapshot snapshot = Substitute.For<Nethermind.Xdc.Types.Snapshot>();
        snapshot.GetSigners().Returns(expectedSigners);

        _snapshotManager.GetSnapshotByBlockNumber(100, spec).Returns(snapshot);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.GetSigners(BlockParameter.Latest);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().BeEquivalentTo(expectedSigners);
    }

    [Test]
    public void GetSigners_ShouldReturnSuccess_WithSpecificBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(50);
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 50;

        _blockTree.FindHeader(50).Returns(header);

        XdcReleaseSpec spec = CreateDummyXdcReleaseSpec();
        _specProvider.GetXdcSpec(50).Returns(spec);

        Address[] expectedSigners = new[] { TestItem.AddressA };
        Nethermind.Xdc.Types.Snapshot snapshot = Substitute.For<Nethermind.Xdc.Types.Snapshot>();
        snapshot.GetSigners().Returns(expectedSigners);

        _snapshotManager.GetSnapshotByBlockNumber(50, spec).Returns(snapshot);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.GetSigners(blockParameter);

        // Assert
        result.Result.Should().Be(Result.Success);
        result.Data.Should().BeEquivalentTo(expectedSigners);
    }

    [Test]
    public void GetSigners_ShouldReturnFail_WhenInvalidBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(-1);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.GetSigners(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetSigners_ShouldReturnFail_WhenHeaderNotFound()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        _blockTree.FindHeader(100).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.GetSigners(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    #endregion

    #region GetMissedRoundsInEpochByBlockNum Tests

    [Test]
    public void GetMissedRoundsInEpochByBlockNum_ShouldReturnFail_WhenInvalidBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(-1);

        // Act
        ResultWrapper<PublicApiMissedRoundsMetadata> result = _rpcModule.GetMissedRoundsInEpochByBlockNum(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetMissedRoundsInEpochByBlockNum_ShouldReturnFail_WhenHeaderNotFound()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        _blockTree.FindHeader(100).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<PublicApiMissedRoundsMetadata> result = _rpcModule.GetMissedRoundsInEpochByBlockNum(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    [Test]
    public void GetMissedRoundsInEpochByBlockNum_ShouldReturnFail_WhenHeaderIsNotXdcHeader()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        BlockHeader header = Build.A.BlockHeader.TestObject;
        header.Number = 100;
        _blockTree.FindHeader(100).Returns(header);

        // Act
        ResultWrapper<PublicApiMissedRoundsMetadata> result = _rpcModule.GetMissedRoundsInEpochByBlockNum(blockParameter);

        // Assert
        result.Result.Should().NotBe(Result.Success);
        result.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    #endregion

    #region GetRewardByAccount Tests

    [Test]
    public void GetRewardByAccount_ShouldThrowNotImplementedException()
    {
        // Act & Assert
        Assert.Throws<NotImplementedException>(() =>
            _rpcModule.GetRewardByAccount(TestItem.AddressA, 0, 100));
    }

    #endregion
}
