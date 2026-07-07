// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
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
    private IRewardsStore _rewardsStore;
    private XdcRpcModule _rpcModule;


    private EpochSwitchInfo[] GenerateEpochSwitchInfos(ulong begin, ulong end, ulong switchEpoch, ulong epochLength)
    {
        List<EpochSwitchInfo> epochSwitchInfos = [];
        for (ulong blockNum = begin; blockNum <= end; blockNum += epochLength)
        {
            ulong epochNumber = blockNum / epochLength;
            if (epochNumber >= switchEpoch)
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

    private IXdcReleaseSpec CreateDummyXdcReleaseSpec(
        ulong? switchEpoch = null,
        ulong? epochLength = null,
        ulong? switchBlock = null,
        int? maxMasternodes = null,
        double? certThreshold = null,
        int? timeoutPeriod = null,
        ulong? minePeriod = null,
        int? configsCount = null)
    {
        List<V2ConfigParams> v2Configs = [];

        int count = configsCount ?? 1;

        for (int i = 0; i < count; i++)
        {
            v2Configs.Add(new V2ConfigParams
            {
                SwitchRound = 0,
                MaxMasternodes = maxMasternodes ?? 108,
                CertificateThreshold = certThreshold ?? 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = timeoutPeriod ?? 30000,
                MinePeriod = minePeriod ?? 2
            });
        }


        XdcReleaseSpec spec = new()
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
            CertificateThreshold = certThreshold ?? 0.667,     // 2/3 majority for certificates

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

            // Feature flags
            IsBlackListingEnabled = false,
            IsTIP2019 = true,
            IsTIPXDCXMiner = false,

            // Other settings
            MergeSignRange = 15,
            BlackListedAddresses = [],

            // V2 configuration parameters
            V2Configs = v2Configs
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
        _rewardsStore = Substitute.For<IRewardsStore>();

        _rpcModule = new XdcRpcModule(
            _blockTree,
            _snapshotManager,
            _specProvider,
            _quorumCertificateManager,
            _epochSwitchManager,
            _votesManager,
            _timeoutCertificateManager,
            _syncInfoManager,
            _rewardsStore);
    }

    [Test]
    public void CalculateBlockInfoByV1EpochNum_ShouldReturnFail_WhenV1EpochIsRequested()
    {
        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.XDPoS_calculateBlockInfoByV1EpochNum(1);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }


    [Test]
    public void GetBlockInfoByV2EpochNum_ShouldReturnSuccess_WhenEpochExists()
    {
        // Arrange
        ulong epochNumber = 5;
        Hash256 expectedHash = TestItem.KeccakA;
        ulong expectedRound = 100;
        ulong expectedBlockNumber = 500;

        BlockRoundInfo blockRoundInfo = new(expectedHash, expectedRound, expectedBlockNumber);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns(blockRoundInfo);

        BlockRoundInfo nextBlockRoundInfo = new(TestItem.KeccakB, 120, 600);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber + 1).Returns(nextBlockRoundInfo);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.XDPoS_getBlockInfoByV2EpochNum(epochNumber);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.EpochBlockHash, Is.EqualTo(expectedHash));
        Assert.That(result.Data.EpochRound, Is.EqualTo((UInt256)expectedRound));
        Assert.That(result.Data.EpochFirstBlockNumber, Is.EqualTo((UInt256)expectedBlockNumber));
        Assert.That(result.Data.EpochLastBlockNumber, Is.EqualTo((UInt256)(nextBlockRoundInfo.BlockNumber - 1)));
        Assert.That(result.Data.EpochConsensusVersion, Is.EqualTo("v2"));
    }

    [Test]
    public void GetBlockInfoByV2EpochNum_ShouldReturnSuccess_WhenNextEpochDoesNotExist()
    {
        // Arrange
        ulong epochNumber = 5;
        Hash256 expectedHash = TestItem.KeccakA;
        ulong expectedRound = 100;
        ulong expectedBlockNumber = 500;

        BlockRoundInfo blockRoundInfo = new(expectedHash, expectedRound, expectedBlockNumber);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns(blockRoundInfo);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber + 1).Returns((BlockRoundInfo?)null);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.XDPoS_getBlockInfoByV2EpochNum(epochNumber);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.EpochLastBlockNumber, Is.Null);
    }

    [Test]
    public void GetBlockInfoByV2EpochNum_ShouldReturnFail_WhenEpochNotFound()
    {
        // Arrange
        ulong epochNumber = 999;
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns((BlockRoundInfo?)null);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.XDPoS_getBlockInfoByV2EpochNum(epochNumber);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }


    [Test]
    public void GetBlockInfoByEpochNum_ShouldReturnFail_WhenEpochNumberBelowSwitchEpoch()
    {
        // Arrange
        ulong epochNumber = 3;
        ulong headNumber = 100;
        ulong switchEpoch = 5;

        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = headNumber;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: switchEpoch, configsCount: (int)epochNumber);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.XDPoS_getBlockInfoByEpochNum(epochNumber);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetBlockInfoByEpochNum_ShouldCallV2Method_WhenEpochNumberAboveOrEqualSwitchEpoch()
    {
        // Arrange
        ulong epochNumber = 10;
        ulong headNumber = 100;
        ulong switchEpoch = 5;

        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = headNumber;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: switchEpoch, configsCount: (int)epochNumber);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        BlockRoundInfo blockRoundInfo = new(TestItem.KeccakA, 100, 500);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber).Returns(blockRoundInfo);

        // Act
        ResultWrapper<EpochNumInfo> result = _rpcModule.XDPoS_getBlockInfoByEpochNum(epochNumber);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        _epochSwitchManager.Received(1).GetBlockByEpochNumber(epochNumber);
    }


    [Test]
    public void GetEpochNumbersBetween_ShouldReturnSuccess_WhenValidRange()
    {
        // Arrange
        ulong begin = 100;
        ulong end = 200;

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
        ResultWrapper<ulong[]> result = _rpcModule.XDPoS_getEpochNumbersBetween(begin, end);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Has.Length.EqualTo(2));
        Assert.That(result.Data, Is.EqualTo(new[] { 100UL, 150UL }));
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenBeginHeaderNotFound()
    {
        // Arrange
        ulong begin = 100;
        ulong end = 200;

        _blockTree.FindHeader(begin).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.XDPoS_getEpochNumbersBetween(begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenEndHeaderNotFound()
    {
        // Arrange
        ulong begin = 100;
        ulong end = 200;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.XDPoS_getEpochNumbersBetween(begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenBeginGreaterThanEnd()
    {
        // Arrange
        ulong begin = 200;
        ulong end = 100;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.XDPoS_getEpochNumbersBetween(begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenRangeExceedsLimit()
    {
        // Arrange
        ulong begin = 100;
        ulong end = 50_101;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().TestObject;
        beginHeader.Number = begin;

        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.XDPoS_getEpochNumbersBetween(begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetEpochNumbersBetween_ShouldReturnFail_WhenHeadersAreNotXdcHeaders()
    {
        // Arrange
        ulong begin = 100;
        ulong end = 200;

        BlockHeader beginHeader = Build.A.BlockHeader.TestObject;
        beginHeader.Number = begin;

        BlockHeader endHeader = Build.A.BlockHeader.TestObject;
        endHeader.Number = end;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<ulong[]> result = _rpcModule.XDPoS_getEpochNumbersBetween(begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }


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

        Dictionary<(ulong Round, Hash256 Hash), Dictionary<Address, Vote>> receivedVotes = [];
        Dictionary<Address, Vote> voteList = [];
        Vote vote1 = new(new BlockRoundInfo(TestItem.KeccakA, 10, 100), 0) { Signer = TestItem.AddressA };
        Vote vote2 = new(new BlockRoundInfo(TestItem.KeccakA, 10, 100), 0) { Signer = TestItem.AddressB };
        voteList[TestItem.AddressA] = vote1;
        voteList[TestItem.AddressB] = vote2;
        receivedVotes[(10UL, TestItem.KeccakA)] = voteList;

        _votesManager.GetReceivedVotes().Returns(receivedVotes);
        _timeoutCertificateManager.GetReceivedTimeouts().Returns(new Dictionary<(ulong, Hash256), Dictionary<Address, Timeout>>());
        _syncInfoManager.GetReceivedSyncInfos().Returns(new Dictionary<(ulong, Hash256), SyncInfoTypes>());

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.XDPoS_getLatestPoolStatus();

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Vote, Is.Not.Null);
        Assert.That(result.Data.Timeout, Is.Not.Null);
        Assert.That(result.Data.SyncInfo, Is.Not.Null);
    }

    [Test]
    public void GetLatestPoolStatus_ShouldReturnFail_WhenNoHead()
    {
        // Arrange
        _blockTree.Head.Returns((Block?)null);

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.XDPoS_getLatestPoolStatus();

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetLatestPoolStatus_ShouldReturnFail_WhenHeaderIsNotXdcHeader()
    {
        // Arrange
        BlockHeader header = Build.A.BlockHeader.TestObject;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.XDPoS_getLatestPoolStatus();

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetLatestPoolStatus_ShouldReturnFail_WhenEpochSwitchInfoIsNull()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);
        _epochSwitchManager.GetEpochSwitchInfo(header).Returns((EpochSwitchInfo?)null);

        // Act
        ResultWrapper<PoolStatus> result = _rpcModule.XDPoS_getLatestPoolStatus();

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }


    [Test]
    public void GetMasternodesByNumber_ShouldReturnSuccess_WithLatestBlockParameter()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        QuorumCertificate qc = new(new BlockRoundInfo(TestItem.KeccakA, 50, 100), null, 50);
        header.ExtraConsensusData = new ExtraFieldsV2(50, qc);

        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: 5, epochLength: 10, configsCount: 200);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] masternodes = new[] { TestItem.AddressA, TestItem.AddressB };
        Address[] penalties = new[] { TestItem.AddressC };
        Address[] standbynodes = new[] { TestItem.AddressD };

        EpochSwitchInfo epochSwitchInfo = new(
            masternodes,
            standbynodes,
            penalties,
            new BlockRoundInfo(TestItem.KeccakA, 50, 100));

        _epochSwitchManager.GetEpochSwitchInfo(header).Returns(epochSwitchInfo);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(BlockParameter.Latest);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Masternodes, Is.EquivalentTo(masternodes));
        Assert.That(result.Data.Penalty, Is.EquivalentTo(penalties));
        Assert.That(result.Data.Standbynodes, Is.EquivalentTo(standbynodes));
        Assert.That(result.Data.Number, Is.EqualTo(100));
        Assert.That(result.Data.Round, Is.EqualTo((UInt256)50));
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

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: 5, epochLength: 10, configsCount: 200);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] masternodes = new[] { TestItem.AddressA };
        EpochSwitchInfo epochSwitchInfo = new(
            masternodes,
            Array.Empty<Address>(),
            Array.Empty<Address>(),
            new BlockRoundInfo(TestItem.KeccakA, 50, 100));

        _epochSwitchManager.GetEpochSwitchInfo(header).Returns(epochSwitchInfo);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(BlockParameter.Finalized);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenFinalizedBlockNotFound()
    {
        // Arrange
        _quorumCertificateManager.HighestKnownCertificate.Returns((QuorumCertificate?)null);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(BlockParameter.Finalized);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenInvalidBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(ulong.MaxValue);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenHeaderNotFound()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        _blockTree.FindHeader(100).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
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
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
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
        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }


    [Test]
    public void GetSigners_ShouldReturnSuccess_WithLatestBlockParameter()
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 100;
        header.Hash = Keccak.OfAnEmptySequenceRlp;

        _blockTree.Head.Returns(Build.A.Block.WithHeader(header).TestObject);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: 5, epochLength: 10, configsCount: 200);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] expectedSigners = new[] { TestItem.AddressA, TestItem.AddressB };
        Snapshot snapshot = new(header.Number, header.Hash!, expectedSigners);

        _snapshotManager.GetSnapshotByBlockNumber(100, spec).Returns(snapshot);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.XDPoS_getSigners(BlockParameter.Latest);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.EquivalentTo(expectedSigners));
    }

    [Test]
    public void GetSigners_ShouldReturnSuccess_WithSpecificBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(50);
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        header.Number = 50;
        header.Hash = Keccak.OfAnEmptySequenceRlp;

        _blockTree.FindHeader(50).Returns(header);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: 5, epochLength: 10, configsCount: 200);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] expectedSigners = new[] { TestItem.AddressA };
        Snapshot snapshot = new(header.Number, header.Hash!, expectedSigners);

        _snapshotManager.GetSnapshotByBlockNumber(50, spec).Returns(snapshot);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.XDPoS_getSigners(blockParameter);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.EquivalentTo(expectedSigners));
    }

    [Test]
    public void GetSigners_ShouldReturnFail_WhenInvalidBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(ulong.MaxValue);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.XDPoS_getSigners(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetSigners_ShouldReturnFail_WhenHeaderNotFound()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        _blockTree.FindHeader(100).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<Address[]> result = _rpcModule.XDPoS_getSigners(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }


    [Test]
    public void GetMissedRoundsInEpochByBlockNum_ShouldReturnFail_WhenInvalidBlockNumber()
    {
        // Arrange
        BlockParameter blockParameter = new(ulong.MaxValue);

        // Act
        ResultWrapper<PublicApiMissedRoundsMetadata> result = _rpcModule.XDPoS_getMissedRoundsInEpochByBlockNum(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetMissedRoundsInEpochByBlockNum_ShouldReturnFail_WhenHeaderNotFound()
    {
        // Arrange
        BlockParameter blockParameter = new(100);
        _blockTree.FindHeader(100).Returns((BlockHeader?)null);

        // Act
        ResultWrapper<PublicApiMissedRoundsMetadata> result = _rpcModule.XDPoS_getMissedRoundsInEpochByBlockNum(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
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
        ResultWrapper<PublicApiMissedRoundsMetadata> result = _rpcModule.XDPoS_getMissedRoundsInEpochByBlockNum(blockParameter);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetRewardByAccount_ShouldReturnSuccess_WhenRewardsExist()
    {
        // Arrange
        Address account = TestItem.AddressA;
        const ulong begin = 100;
        const ulong end = 200;
        const ulong epoch1 = 120;
        const ulong epoch2 = 180;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().WithNumber(begin).TestObject;
        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().WithNumber(end).TestObject;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        EpochSwitchInfo[] epochSwitchInfos =
        [
            new EpochSwitchInfo(Array.Empty<Address>(), Array.Empty<Address>(), Array.Empty<Address>(), new BlockRoundInfo(TestItem.KeccakA, 1, (long)epoch1)),
            new EpochSwitchInfo(Array.Empty<Address>(), Array.Empty<Address>(), Array.Empty<Address>(), new BlockRoundInfo(TestItem.KeccakB, 2, (long)epoch2)),
        ];

        _epochSwitchManager.GetEpochSwitchInfoBetween(beginHeader, endHeader).Returns(epochSwitchInfos);
        _rewardsStore.HasEpochRewards(TestItem.KeccakA).Returns(true);
        _rewardsStore.HasEpochRewards(TestItem.KeccakB).Returns(true);
        _rewardsStore.TryGetAccountReward(account, TestItem.KeccakA, out Arg.Any<UInt256>())
            .Returns(callInfo =>
            {
                callInfo[2] = (UInt256)10;
                return true;
            });
        _rewardsStore.TryGetAccountReward(account, TestItem.KeccakB, out Arg.Any<UInt256>())
            .Returns(callInfo =>
            {
                callInfo[2] = (UInt256)20;
                return true;
            });

        // Act
        ResultWrapper<AccountRewardResponse> result = _rpcModule.XDPoS_getRewardByAccount(account, begin, end);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.EpochRewards, Is.Not.Null);
        Assert.That(result.Data.EpochRewards!.Length, Is.EqualTo(2));
        Assert.That(result.Data.Total, Is.Not.Null);
        Assert.That(result.Data.Total!.Address, Is.EqualTo(account));
        Assert.That(result.Data.Total.TotalAccountReward, Is.EqualTo((UInt256)30));
    }

    [Test]
    public void GetRewardByAccount_ShouldReturnFail_WhenRewardsMissingForEpoch()
    {
        // Arrange
        Address account = TestItem.AddressA;
        const ulong begin = 100;
        const ulong end = 200;
        const ulong epoch = 120;

        XdcBlockHeader beginHeader = Build.A.XdcBlockHeader().WithNumber(begin).TestObject;
        XdcBlockHeader endHeader = Build.A.XdcBlockHeader().WithNumber(end).TestObject;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        EpochSwitchInfo[] epochSwitchInfos =
        [
            new EpochSwitchInfo(Array.Empty<Address>(), Array.Empty<Address>(), Array.Empty<Address>(), new BlockRoundInfo(TestItem.KeccakA, 1, (long)epoch)),
        ];

        _epochSwitchManager.GetEpochSwitchInfoBetween(beginHeader, endHeader).Returns(epochSwitchInfos);
        _rewardsStore.HasEpochRewards(TestItem.KeccakA).Returns(false);

        // Act
        ResultWrapper<AccountRewardResponse> result = _rpcModule.XDPoS_getRewardByAccount(account, begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void GetRewardByAccount_ShouldReturnFail_WhenHeaderIsNotXdcHeader()
    {
        // Arrange
        Address account = TestItem.AddressA;
        const ulong begin = 100;
        const ulong end = 200;

        BlockHeader beginHeader = Build.A.BlockHeader.WithNumber(begin).TestObject;
        BlockHeader endHeader = Build.A.BlockHeader.WithNumber(end).TestObject;

        _blockTree.FindHeader(begin).Returns(beginHeader);
        _blockTree.FindHeader(end).Returns(endHeader);

        // Act
        ResultWrapper<AccountRewardResponse> result = _rpcModule.XDPoS_getRewardByAccount(account, begin, end);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

}
