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
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

[TestFixture, NonParallelizable]
public class RpcModuleTests
{
    private const ulong FinalizedBlockNumber = 98;
    private const ulong HeadBlockNumber = 100;

    private IBlockTree _blockTree;
    private ISnapshotManager _snapshotManager;
    private ISpecProvider _specProvider;
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
        _epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        _votesManager = Substitute.For<IVotesManager>();
        _timeoutCertificateManager = Substitute.For<ITimeoutCertificateManager>();
        _syncInfoManager = Substitute.For<ISyncInfoManager>();
        _rewardsStore = Substitute.For<IRewardsStore>();

        _rpcModule = new XdcRpcModule(
            _blockTree,
            _snapshotManager,
            _specProvider,
            _epochSwitchManager,
            _votesManager,
            _timeoutCertificateManager,
            _syncInfoManager,
            _rewardsStore);
    }

    [Test]
    public void BuildRpcSnapshot_ShouldUseSnapshotIdentity()
    {
        const long snapshotNumber = 104_357_250;
        Snapshot snapshot = new(snapshotNumber, TestItem.KeccakA, [TestItem.AddressA]);

        PublicApiSnapshot result = snapshot.BuildRpcSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Number, Is.EqualTo((ulong)snapshotNumber));
            Assert.That(result.Hash, Is.EqualTo(TestItem.KeccakA));
            Assert.That(result.Signers, Is.EquivalentTo(new[] { TestItem.AddressA }));
        }
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
    public void GetMasternodesByNumber_ShouldResolveFinalizedBlock_FromTheCommittedTip()
    {
        XdcBlockHeader finalizedHeader = ArrangeChainWithFinalizedTip()[FinalizedBlockNumber];

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchEpoch: 5, epochLength: 10, configsCount: 200);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] masternodes = new[] { TestItem.AddressA };
        EpochSwitchInfo epochSwitchInfo = new(
            masternodes,
            Array.Empty<Address>(),
            Array.Empty<Address>(),
            new BlockRoundInfo(finalizedHeader.Hash!, FinalizedBlockNumber, (long)FinalizedBlockNumber));

        _epochSwitchManager.GetEpochSwitchInfo(finalizedHeader).Returns(epochSwitchInfo);

        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(BlockParameter.Finalized);

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data!.Number, Is.EqualTo(FinalizedBlockNumber));
            Assert.That(result.Data.Round, Is.EqualTo((UInt256)FinalizedBlockNumber));
            Assert.That(result.Data.Masternodes, Is.EquivalentTo(masternodes));
        }
    }

    [Test]
    public void GetMasternodesByNumber_ShouldReturnFail_WhenFinalizedBlockNotFound()
    {
        _blockTree.FinalizedHash.Returns((Hash256?)null);

        ResultWrapper<MasternodesStatus> result = _rpcModule.XDPoS_getMasternodesByNumber(BlockParameter.Finalized);

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


    [TestCase(null, false)]
    [TestCase(100UL, false)]
    [TestCase(99UL, false)]
    [TestCase(98UL, true)]
    [TestCase(97UL, true)]
    public void GetV2BlockByNumber_ShouldReportCommitted_OnlyUpToFinalizedBlock(ulong? blockNumber, bool expectedCommitted)
    {
        ArrangeChainWithFinalizedTip();

        ResultWrapper<V2BlockInfo> result = _rpcModule.XDPoS_getV2BlockByNumber(
            blockNumber is null ? BlockParameter.Latest : new BlockParameter(blockNumber.Value));

        AssertCommitted(result, expectedCommitted);
    }

    [TestCase(null, false)]
    [TestCase(99UL, false)]
    [TestCase(98UL, true)]
    public void GetV2BlockByHash_ShouldReportCommitted_OnlyUpToFinalizedBlock(ulong? blockNumber, bool expectedCommitted)
    {
        Dictionary<ulong, XdcBlockHeader> headers = ArrangeChainWithFinalizedTip();

        ResultWrapper<V2BlockInfo> result = _rpcModule.XDPoS_getV2BlockByHash(
            blockNumber is null ? BlockParameter.Latest : new BlockParameter(headers[blockNumber.Value].Hash!));

        AssertCommitted(result, expectedCommitted);
    }

    [Test]
    public void GetV2BlockByHash_ShouldNotReportCommitted_ForBlockOffTheCanonicalChain()
    {
        ArrangeChainWithFinalizedTip();

        XdcBlockHeader forkHeader = Build.A.XdcBlockHeader()
            .WithNumber(FinalizedBlockNumber)
            .WithParentHash(TestItem.KeccakA)
            .WithExtraConsensusData(new ExtraFieldsV2(FinalizedBlockNumber, Build.A.QuorumCertificate().TestObject))
            .TestObject;

        _blockTree.FindHeader(forkHeader.Hash!).Returns(forkHeader);
        _blockTree.IsMainChain(forkHeader).Returns(false);

        ResultWrapper<V2BlockInfo> result = _rpcModule.XDPoS_getV2BlockByHash(new BlockParameter(forkHeader.Hash!));

        AssertCommitted(result, false);
    }

    [Test]
    public void GetV2BlockByNumber_ShouldReturnError_WhenNoFinalizedBlock()
    {
        ArrangeChainWithFinalizedTip();
        _blockTree.FinalizedHash.Returns((Hash256?)null);

        ResultWrapper<V2BlockInfo> result = _rpcModule.XDPoS_getV2BlockByNumber(BlockParameter.Latest);

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data!.Committed, Is.False);
            Assert.That(result.Data.Error, Is.Not.Null);
        }
    }

    /// <summary>
    /// Sets up a chain whose head is two rounds ahead of the committed (finalized) tip, as the XDPoS 2.0 commit rule
    /// leaves it. Each header carries its own block number as its round.
    /// </summary>
    /// <returns>The headers of the chain, keyed by block number.</returns>
    private Dictionary<ulong, XdcBlockHeader> ArrangeChainWithFinalizedTip()
    {
        Dictionary<ulong, XdcBlockHeader> headers = [];
        for (ulong number = FinalizedBlockNumber - 1; number <= HeadBlockNumber; number++)
        {
            XdcBlockHeader header = Build.A.XdcBlockHeader()
                .WithNumber(number)
                .WithExtraConsensusData(new ExtraFieldsV2(number, Build.A.QuorumCertificate().TestObject))
                .TestObject;

            headers[number] = header;
            _blockTree.FindHeader(number).Returns(header);
            _blockTree.FindHeader(header.Hash!).Returns(header);
            _blockTree.FindHeader(header.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(header);
            _blockTree.IsMainChain(header).Returns(true);
        }

        _blockTree.Head.Returns(Build.A.Block.WithHeader(headers[HeadBlockNumber]).TestObject);
        _blockTree.FinalizedHash.Returns(headers[FinalizedBlockNumber].Hash);
        _blockTree.LastFinalizedBlockLevel.Returns(FinalizedBlockNumber);

        return headers;
    }

    private static void AssertCommitted(ResultWrapper<V2BlockInfo> result, bool expectedCommitted)
    {
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data!.Committed, Is.EqualTo(expectedCommitted));
            Assert.That(result.Data.Error, Is.Null);
        }
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
    public void GetMissedRoundsInEpochByBlockNum_RoundGap_ReturnsOnlySkippedRounds()
    {
        const long epochBlockNumber = 1800;
        const ulong epochRound = 900;
        XdcBlockHeader epochHeader = BuildHeader(epochBlockNumber, epochRound, Hash256.Zero);
        XdcBlockHeader block1801 = BuildHeader(1801, 901, epochHeader.Hash!);
        XdcBlockHeader block1802 = BuildHeader(1802, 902, block1801.Hash!);
        XdcBlockHeader block1803 = BuildHeader(1803, 905, block1802.Hash!);
        Address[] masternodes = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];

        _blockTree.FindHeader(1803).Returns(block1803);
        _blockTree.FindHeader(block1802.Hash!).Returns(block1802);
        _blockTree.FindHeader(block1801.Hash!).Returns(block1801);
        _blockTree.FindHeader(epochHeader.Hash!).Returns(epochHeader);
        _epochSwitchManager.GetEpochSwitchInfo(block1803).Returns(new EpochSwitchInfo(
            masternodes,
            [],
            [],
            new BlockRoundInfo(epochHeader.Hash!, epochRound, epochBlockNumber)));
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(CreateDummyXdcReleaseSpec(epochLength: 900));

        ResultWrapper<PublicApiMissedRoundsMetadata> result =
            _rpcModule.XDPoS_getMissedRoundsInEpochByBlockNum(new BlockParameter(1803));

        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.Not.Null);
        PublicApiMissedRoundsMetadata data = result.Data!;
        Assert.That(data.MissedRounds, Is.Not.Null);
        MissedRoundInfo[] missedRounds = data.MissedRounds!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(data.EpochRound, Is.EqualTo(epochRound));
            Assert.That(data.EpochBlockNumber, Is.EqualTo((UInt256)epochBlockNumber));
            Assert.That(missedRounds, Has.Length.EqualTo(2));
            Assert.That(missedRounds[0].Round, Is.EqualTo(903));
            Assert.That(missedRounds[0].Miner, Is.EqualTo(TestItem.AddressA));
            Assert.That(missedRounds[0].CurrentBlockHash, Is.EqualTo(block1803.Hash));
            Assert.That(missedRounds[0].CurrentBlockNum, Is.EqualTo((UInt256)1803));
            Assert.That(missedRounds[0].ParentBlockHash, Is.EqualTo(block1802.Hash));
            Assert.That(missedRounds[0].ParentBlockNum, Is.EqualTo((UInt256)1802));
            Assert.That(missedRounds[1].Round, Is.EqualTo(904));
            Assert.That(missedRounds[1].Miner, Is.EqualTo(TestItem.AddressB));
            Assert.That(missedRounds[1].CurrentBlockHash, Is.EqualTo(block1803.Hash));
            Assert.That(missedRounds[1].CurrentBlockNum, Is.EqualTo((UInt256)1803));
            Assert.That(missedRounds[1].ParentBlockHash, Is.EqualTo(block1802.Hash));
            Assert.That(missedRounds[1].ParentBlockNum, Is.EqualTo((UInt256)1802));
        }

        static XdcBlockHeader BuildHeader(ulong number, ulong round, Hash256 parentHash) =>
            Build.A.XdcBlockHeader()
                .WithNumber(number)
                .WithParentHash(parentHash)
                .WithExtraConsensusData(new ExtraFieldsV2(round, Build.A.QuorumCertificate().TestObject))
                .TestObject;
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
        Address owner = TestItem.AddressB;
        Address foundation = TestItem.AddressC;
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

        XdcEpochRewards emptyEpochBreakdown = new()
        {
            Signers = new()
            {
                [owner.ToString()] = new XdcRewardLog
                {
                    Reward = "99",
                    Sign = 1,
                },
            },
        };
        XdcEpochRewards rewardedEpochBreakdown = new()
        {
            Signers = new()
            {
                [account.ToString()] = new XdcRewardLog
                {
                    Reward = "20",
                    Sign = 5,
                },
            },
            Rewards = new()
            {
                [account.ToString()] = new()
                {
                    [owner.ToString()] = "18",
                    [foundation.ToString()] = "2",
                },
            },
        };

        _epochSwitchManager.GetEpochSwitchInfoBetween(beginHeader, endHeader).Returns(epochSwitchInfos);
        _rewardsStore.TryGetEpochRewards(TestItem.KeccakA, out Arg.Any<XdcEpochRewards?>())
            .Returns(callInfo =>
            {
                callInfo[1] = emptyEpochBreakdown;
                return true;
            });
        _rewardsStore.TryGetEpochRewards(TestItem.KeccakB, out Arg.Any<XdcEpochRewards?>())
            .Returns(callInfo =>
            {
                callInfo[1] = rewardedEpochBreakdown;
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
        Assert.That(result.Data.EpochRewards![1].DelegatedReward, Is.Not.Null);
        Assert.That(result.Data.Total!.TotalDelegatedReward, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.EpochRewards[0].AccountStatus, Is.EqualTo(""));
            Assert.That(result.Data.EpochRewards[0].AccountReward, Is.Null);
            Assert.That(result.Data.EpochRewards[1].AccountStatus, Is.EqualTo(XdcConstants.RpcAccountStatusMasternode));
            Assert.That(result.Data.EpochRewards[1].AccountReward, Is.EqualTo((UInt256)20));
            Assert.That(result.Data.EpochRewards[1].DelegatedReward![owner.ToString()], Is.EqualTo((UInt256)18));
            Assert.That(result.Data.EpochRewards[1].DelegatedReward![foundation.ToString()], Is.EqualTo((UInt256)2));
            Assert.That(result.Data.Total!.Address, Is.EqualTo(account));
            Assert.That(result.Data.Total.TotalAccountReward, Is.EqualTo((UInt256)20));
            Assert.That(result.Data.Total.TotalDelegatedReward![owner.ToString()], Is.EqualTo((UInt256)18));
            Assert.That(result.Data.Total.TotalDelegatedReward![foundation.ToString()], Is.EqualTo((UInt256)2));
        }
    }

    [TestCase(
        nameof(XdcEpochRewards.Signers),
        nameof(XdcEpochRewards.Rewards),
        XdcConstants.RpcAccountStatusMasternode)]
    [TestCase(
        nameof(XdcEpochRewards.SignersProtector),
        nameof(XdcEpochRewards.RewardsProtector),
        XdcConstants.RpcAccountStatusProtector)]
    [TestCase(
        nameof(XdcEpochRewards.SignersObserver),
        nameof(XdcEpochRewards.RewardsObserver),
        XdcConstants.RpcAccountStatusObserver)]
    public void BuildAccountEpochReward_Signer_ReturnsSignerAndDelegatedRewards(
        string signerSection,
        string rewardSection,
        string expectedStatus)
    {
        Address validator = Address.FromNumber(1);
        Address owner = Address.FromNumber(2);
        Address foundation = Address.FromNumber(3);
        const ulong epoch = 1795;

        XdcEpochRewards epochRewardData = new();
        GetSignerSection(epochRewardData, signerSection)[validator.ToString()] = new XdcRewardLog
        {
            Reward = "57000000000000000000",
            Sign = 59,
        };
        GetRewardSection(epochRewardData, rewardSection)[validator.ToString()] = new()
        {
            [owner.ToString()] = "51300000000000000000",
            [foundation.ToString()] = "5700000000000000000",
        };

        AccountEpochReward epochReward = epochRewardData.BuildAccountEpochReward(validator, epoch);

        Assert.That(epochReward.DelegatedReward, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(epochReward.EpochBlockNum, Is.EqualTo(epoch));
            Assert.That(epochReward.Address, Is.EqualTo(validator));
            Assert.That(epochReward.AccountStatus, Is.EqualTo(expectedStatus));
            Assert.That(epochReward.AccountReward, Is.EqualTo(UInt256.Parse("57000000000000000000")));
            Assert.That(epochReward.DelegatedReward[owner.ToString()], Is.EqualTo(UInt256.Parse("51300000000000000000")));
            Assert.That(epochReward.DelegatedReward![foundation.ToString()], Is.EqualTo(UInt256.Parse("5700000000000000000")));
        }
    }

    [Test]
    public void BuildAccountEpochReward_DelegateOnly_ReturnsEmptyEpochReward()
    {
        Address validator = Address.FromNumber(1);
        Address owner = Address.FromNumber(2);
        const ulong epoch = 1795;

        XdcEpochRewards epochRewardData = new()
        {
            Signers = new()
            {
                [validator.ToString()] = new XdcRewardLog
                {
                    Reward = "57000000000000000000",
                    Sign = 59,
                },
            },
            Rewards = new()
            {
                [validator.ToString()] = new()
                {
                    [owner.ToString()] = "51300000000000000000",
                },
            },
        };

        AccountEpochReward epochReward = epochRewardData.BuildAccountEpochReward(owner, epoch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(epochReward.EpochBlockNum, Is.EqualTo(epoch));
            Assert.That(epochReward.Address, Is.EqualTo(owner));
            Assert.That(epochReward.AccountStatus, Is.EqualTo(""));
            Assert.That(epochReward.AccountReward, Is.Null);
            Assert.That(epochReward.DelegatedReward, Is.Empty);
        }
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
        _rewardsStore.TryGetEpochRewards(TestItem.KeccakA, out Arg.Any<XdcEpochRewards?>())
            .Returns(false);

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

    [TestCase(BlockScopedEndpoint.GetSnapshot)]
    [TestCase(BlockScopedEndpoint.GetSnapshotAtHash)]
    public void SnapshotEndpoints_ShouldReturnSuccess_WhenSnapshotExists(BlockScopedEndpoint endpoint)
    {
        // Arrange
        XdcBlockHeader header = BuildV2Header(50, 50);
        BlockParameter blockParam = RegisterHeader(endpoint, header);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] candidates = [TestItem.AddressA, TestItem.AddressB];
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, spec).Returns(new Snapshot(header.Number, header.Hash!, candidates));

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        PublicApiSnapshot snapshot = (PublicApiSnapshot)result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Number, Is.EqualTo(header.Number));
            Assert.That(snapshot.Hash, Is.EqualTo(header.Hash));
            Assert.That(snapshot.Signers, Is.EquivalentTo(candidates));
        }
    }

    [Test]
    public void GetSignersAtHash_ShouldReturnSuccess_WithSpecificBlockHash()
    {
        // Arrange
        XdcBlockHeader header = BuildV2Header(50, 50);
        _blockTree.FindHeader(header.Hash!).Returns(header);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        Address[] expectedSigners = [TestItem.AddressA, TestItem.AddressB];
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, spec).Returns(new Snapshot(header.Number, header.Hash!, expectedSigners));

        // Act
        ResultWrapper<Address[]> result = _rpcModule.XDPoS_getSignersAtHash(new BlockParameter(header.Hash!));

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        Assert.That(result.Data, Is.EquivalentTo(expectedSigners));
    }

    [TestCase(BlockScopedEndpoint.GetSnapshot)]
    [TestCase(BlockScopedEndpoint.GetSnapshotAtHash)]
    [TestCase(BlockScopedEndpoint.GetSignersAtHash)]
    public void SnapshotEndpoints_ShouldReturnFail_WhenBlockIsV1(BlockScopedEndpoint endpoint)
    {
        // Arrange
        const ulong switchBlock = 10;
        XdcBlockHeader header = BuildV2Header(switchBlock - 1, 5);
        BlockParameter blockParam = RegisterHeader(endpoint, header);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(switchBlock: switchBlock);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, spec).Returns(new Snapshot(header.Number, header.Hash!, [TestItem.AddressA]));

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [TestCase(BlockScopedEndpoint.GetSnapshot)]
    [TestCase(BlockScopedEndpoint.GetSnapshotAtHash)]
    [TestCase(BlockScopedEndpoint.GetSignersAtHash)]
    public void SnapshotEndpoints_ShouldReturnFail_WhenSnapshotNotFound(BlockScopedEndpoint endpoint)
    {
        // Arrange
        XdcBlockHeader header = BuildV2Header(100, 100);
        BlockParameter blockParam = RegisterHeader(endpoint, header);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(CreateDummyXdcReleaseSpec());

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [TestCase(BlockScopedEndpoint.GetV2BlockByNumber, true)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByNumber, false)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByHash, true)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByHash, false)]
    public void V2BlockEndpoints_ShouldReturnSuccess_WhenBlockExists(BlockScopedEndpoint endpoint, bool committed)
    {
        // Arrange
        const ulong blockNumber = 100;
        const ulong round = 97;
        XdcBlockHeader header = BuildV2Header(blockNumber, round);
        BlockParameter blockParam = RegisterHeader(endpoint, header);

        _quorumCertificateManager.HighestKnownCertificate.Returns(Build.A.QuorumCertificate()
            .WithBlockInfo(new BlockRoundInfo(TestItem.KeccakA, round, committed ? blockNumber : blockNumber - 1))
            .TestObject);

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        V2BlockInfo blockInfo = (V2BlockInfo)result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockInfo.Hash, Is.EqualTo(header.Hash));
            Assert.That(blockInfo.ParentHash, Is.EqualTo(header.ParentHash));
            Assert.That(blockInfo.Number, Is.EqualTo((UInt256)blockNumber));
            Assert.That(blockInfo.Round, Is.EqualTo((UInt256)round));
            Assert.That(blockInfo.Committed, Is.EqualTo(committed));
            Assert.That(blockInfo.Miner, Is.EqualTo(header.Beneficiary));
            Assert.That(blockInfo.Timestamp, Is.EqualTo(header.Timestamp));
            Assert.That(blockInfo.Error, Is.Null);
            Assert.That(DecodeHeader(blockInfo.EncodedRLP!), Is.EqualTo(header).UsingXdcComparer(compareHash: false));
        }
    }

    [TestCase(BlockScopedEndpoint.GetV2BlockByNumber)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByHash)]
    public void V2BlockEndpoints_ShouldReportError_WhenNoCommittedBlockIsKnown(BlockScopedEndpoint endpoint)
    {
        // Arrange
        XdcBlockHeader header = BuildV2Header(100, 97);
        BlockParameter blockParam = RegisterHeader(endpoint, header);

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        V2BlockInfo blockInfo = (V2BlockInfo)result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockInfo.Hash, Is.EqualTo(header.Hash));
            Assert.That(blockInfo.Error, Is.Not.Null);
            Assert.That(blockInfo.Committed, Is.False);
            Assert.That(blockInfo.EncodedRLP, Is.Null);
        }
    }

    [TestCase(BlockScopedEndpoint.GetV2BlockByNumber)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByHash)]
    public void V2BlockEndpoints_ShouldReturnFail_WhenConsensusDataIsMissing(BlockScopedEndpoint endpoint)
    {
        // Arrange
        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(100).TestObject;
        BlockParameter blockParam = RegisterHeader(endpoint, header);

        _quorumCertificateManager.HighestKnownCertificate.Returns(Build.A.QuorumCertificate()
            .WithBlockInfo(new BlockRoundInfo(TestItem.KeccakA, 97, 100))
            .TestObject);

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void BlockScopedEndpoints_ShouldReturnSuccess_WhenResolvingHead(
        [Values] BlockScopedEndpoint endpoint,
        [Values] bool useLatestBlockParameter)
    {
        // Arrange
        XdcBlockHeader head = BuildV2Header(100, 97);
        _blockTree.Head.Returns(Build.A.Block.WithHeader(head).TestObject);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        _snapshotManager.GetSnapshotByBlockNumber(head.Number, spec).Returns(new Snapshot(head.Number, head.Hash!, [TestItem.AddressA]));
        _quorumCertificateManager.HighestKnownCertificate.Returns(Build.A.QuorumCertificate()
            .WithBlockInfo(new BlockRoundInfo(head.Hash!, 97, head.Number))
            .TestObject);

        // Act
        IResultWrapper result = Invoke(endpoint, useLatestBlockParameter ? BlockParameter.Latest : null);

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
    }

    [Test]
    public void BlockScopedEndpoints_ShouldReturnFail_WhenHeadIsUnknown(
        [Values] BlockScopedEndpoint endpoint,
        [Values] bool useLatestBlockParameter)
    {
        // Act
        IResultWrapper result = Invoke(endpoint, useLatestBlockParameter ? BlockParameter.Latest : null);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void BlockScopedEndpoints_ShouldReturnFail_WhenHeaderNotFound([Values] BlockScopedEndpoint endpoint)
    {
        // Act
        IResultWrapper result = Invoke(endpoint, UnknownBlockParameter(endpoint));

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [TestCase(BlockScopedEndpoint.GetSnapshotAtHash)]
    [TestCase(BlockScopedEndpoint.GetSignersAtHash)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByHash)]
    public void HashBasedEndpoints_ShouldReturnFail_WhenBlockHashIsMissing(BlockScopedEndpoint endpoint)
    {
        // Act
        IResultWrapper result = Invoke(endpoint, BlockParameter.Finalized);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [TestCase(BlockScopedEndpoint.GetSnapshot)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByNumber)]
    public void NumberBasedEndpoints_ShouldReturnFail_WhenBlockNumberIsMissing(BlockScopedEndpoint endpoint)
    {
        // Act
        IResultWrapper result = Invoke(endpoint, BlockParameter.Finalized);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [TestCase(BlockScopedEndpoint.GetSnapshot)]
    [TestCase(BlockScopedEndpoint.GetSnapshotAtHash)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByNumber)]
    [TestCase(BlockScopedEndpoint.GetV2BlockByHash)]
    public void BlockScopedEndpoints_ShouldReturnFail_WhenHeaderIsNotXdcHeader(BlockScopedEndpoint endpoint)
    {
        // Arrange
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        BlockParameter blockParam = RegisterHeader(endpoint, header);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec();
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        _snapshotManager.GetSnapshotByBlockNumber(header.Number, spec).Returns(new Snapshot(header.Number, header.Hash!, [TestItem.AddressA]));
        _quorumCertificateManager.HighestKnownCertificate.Returns(Build.A.QuorumCertificate()
            .WithBlockInfo(new BlockRoundInfo(TestItem.KeccakA, 97, header.Number))
            .TestObject);

        // Act
        IResultWrapper result = Invoke(endpoint, blockParam);

        // Assert
        Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InternalError));
    }

    [Test]
    public void NetworkInformation_ShouldProjectSpecOfHeadBlock()
    {
        // Arrange
        const ulong networkId = 51;
        const ulong epochLength = 900;
        const ulong minePeriod = 2;
        const ulong switchEpoch = 5;
        const ulong switchBlock = 10;

        XdcBlockHeader head = BuildV2Header(100, 97);
        _blockTree.Head.Returns(Build.A.Block.WithHeader(head).TestObject);

        IXdcReleaseSpec spec = CreateDummyXdcReleaseSpec(
            switchEpoch: switchEpoch,
            epochLength: epochLength,
            switchBlock: switchBlock,
            minePeriod: minePeriod);
        spec.MasternodeVotingContract = TestItem.AddressA;
        spec.XDCXLendingAddressBinary = TestItem.AddressB;
        spec.XDCXAddressBinary = TestItem.AddressC;

        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        _specProvider.NetworkId.Returns(networkId);

        // Act
        ResultWrapper<NetworkInformation> result = _rpcModule.XDPoS_networkInformation();

        // Assert
        Assert.That(result.Result, Is.EqualTo(Result.Success));
        NetworkInformation info = result.Data!;
        Assert.That(info.ConsensusConfigs, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.NetworkId, Is.EqualTo((UInt256)networkId));
            Assert.That(info.XDCValidatorAddress, Is.EqualTo(TestItem.AddressA));
            Assert.That(info.LendingAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(info.XDCXListingAddress, Is.EqualTo(TestItem.AddressC));
            Assert.That(info.ConsensusConfigs!.Epoch, Is.EqualTo(epochLength));
            Assert.That(info.ConsensusConfigs.Gap, Is.EqualTo(spec.Gap));
            Assert.That(info.ConsensusConfigs.Period, Is.EqualTo(minePeriod));
            Assert.That(info.ConsensusConfigs.Reward, Is.EqualTo(spec.Reward));
            Assert.That(info.ConsensusConfigs.SwitchEpoch, Is.EqualTo(switchEpoch));
            Assert.That(info.ConsensusConfigs.SwitchBlock, Is.EqualTo(switchBlock));
            Assert.That(info.ConsensusConfigs.V2Configs, Is.SameAs(spec.V2Configs));
        }
    }

    /// <summary>
    /// XDPoS endpoints that resolve a header from a <see cref="BlockParameter"/>, either by number or by hash.
    /// </summary>
    public enum BlockScopedEndpoint
    {
        GetSnapshot,
        GetSnapshotAtHash,
        GetSignersAtHash,
        GetV2BlockByNumber,
        GetV2BlockByHash
    }

    private IResultWrapper Invoke(BlockScopedEndpoint endpoint, BlockParameter? blockParam) => endpoint switch
    {
        BlockScopedEndpoint.GetSnapshot => _rpcModule.XDPoS_getSnapshot(blockParam!),
        BlockScopedEndpoint.GetSnapshotAtHash => _rpcModule.XDPoS_getSnapshotAtHash(blockParam!),
        BlockScopedEndpoint.GetSignersAtHash => _rpcModule.XDPoS_getSignersAtHash(blockParam!),
        BlockScopedEndpoint.GetV2BlockByNumber => _rpcModule.XDPoS_getV2BlockByNumber(blockParam!),
        BlockScopedEndpoint.GetV2BlockByHash => _rpcModule.XDPoS_getV2BlockByHash(blockParam!),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null)
    };

    private static bool IsHashBased(BlockScopedEndpoint endpoint) =>
        endpoint is BlockScopedEndpoint.GetSnapshotAtHash
            or BlockScopedEndpoint.GetSignersAtHash
            or BlockScopedEndpoint.GetV2BlockByHash;

    /// <summary>
    /// Makes <paramref name="header"/> resolvable through the block tree and returns the parameter
    /// that <paramref name="endpoint"/> expects for it.
    /// </summary>
    private BlockParameter RegisterHeader(BlockScopedEndpoint endpoint, BlockHeader header)
    {
        if (IsHashBased(endpoint))
        {
            _blockTree.FindHeader(header.Hash!).Returns(header);
            return new BlockParameter(header.Hash!);
        }

        _blockTree.FindHeader(header.Number).Returns(header);
        return new BlockParameter(header.Number);
    }

    private static BlockParameter UnknownBlockParameter(BlockScopedEndpoint endpoint) =>
        IsHashBased(endpoint) ? new BlockParameter(TestItem.KeccakF) : new BlockParameter(ulong.MaxValue);

    private static XdcBlockHeader BuildV2Header(ulong number, ulong round) =>
        Build.A.XdcBlockHeader()
            .WithNumber(number)
            .WithExtraFieldsV2(new ExtraFieldsV2(round, Build.A.QuorumCertificate().TestObject))
            .TestObject;

    private static XdcBlockHeader DecodeHeader(string encodedRlp)
    {
        RlpReader reader = new(Convert.FromBase64String(encodedRlp));
        return (XdcBlockHeader)new XdcHeaderDecoder().Decode(ref reader)!;
    }

    private static Dictionary<string, XdcRewardLog> GetSignerSection(XdcEpochRewards rewards, string section) =>
        section switch
        {
            nameof(XdcEpochRewards.Signers) => rewards.Signers,
            nameof(XdcEpochRewards.SignersProtector) => rewards.SignersProtector,
            nameof(XdcEpochRewards.SignersObserver) => rewards.SignersObserver,
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
        };

    private static Dictionary<string, Dictionary<string, string>> GetRewardSection(XdcEpochRewards rewards, string section) =>
        section switch
        {
            nameof(XdcEpochRewards.Rewards) => rewards.Rewards,
            nameof(XdcEpochRewards.RewardsProtector) => rewards.RewardsProtector,
            nameof(XdcEpochRewards.RewardsObserver) => rewards.RewardsObserver,
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
        };
}
