// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.RPC;

internal class XdcRpcModule(IBlockTree tree, ISnapshotManager snapshotManager, ISpecProvider specProvider, IQuorumCertificateManager quorumCertificateManager, IEpochSwitchManager epochSwitchManager, IVotesManager voteManager, ITimeoutCertificateManager timeoutCertificateManager, ISyncInfoManager syncInfoManager, IRewardsStore rewardsStore) : IXdcRpcModule
{
    public ResultWrapper<EpochNumInfo> XDPoS_calculateBlockInfoByV1EpochNum(ulong targetEpochNum) =>
        ResultWrapper<EpochNumInfo>.Fail("V1 epoch is not supported");

    public ResultWrapper<EpochNumInfo> XDPoS_getBlockInfoByEpochNum(ulong epochNumber)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(tree.Head?.Header?.Number ?? 0);

        return epochNumber < (ulong)spec.SwitchEpoch ?
            XDPoS_calculateBlockInfoByV1EpochNum(epochNumber) :
            XDPoS_getBlockInfoByV2EpochNum(epochNumber);
    }

    public ResultWrapper<EpochNumInfo> XDPoS_getBlockInfoByV2EpochNum(ulong epochNumber)
    {
        BlockRoundInfo? thisEpoch = epochSwitchManager.GetBlockByEpochNumber(epochNumber);
        if (thisEpoch is null)
        {
            return ResultWrapper<EpochNumInfo>.Fail($"Cannot find epoch {epochNumber}");
        }

        EpochNumInfo info = new()
        {
            EpochBlockHash = thisEpoch.Hash,
            EpochRound = thisEpoch.Round,
            EpochFirstBlockNumber = (UInt256)thisEpoch.BlockNumber,
            EpochConsensusVersion = "v2"
        };

        BlockRoundInfo? nextEpoch = epochSwitchManager.GetBlockByEpochNumber(epochNumber + 1);
        if (nextEpoch != null)
        {
            info.EpochLastBlockNumber = (UInt256)(nextEpoch.BlockNumber - 1);
        }

        return ResultWrapper<EpochNumInfo>.Success(info);
    }

    public ResultWrapper<ulong[]> XDPoS_getEpochNumbersBetween(long begin, long end)
    {
        BlockHeader beginHeader = tree.FindHeader(begin);
        if (beginHeader is null)
        {
            return ResultWrapper<ulong[]>.Fail($"illegal begin block number {begin}");
        }

        BlockHeader endHeader = tree.FindHeader(end);
        if (endHeader is null)
        {
            return ResultWrapper<ulong[]>.Fail($"illegal end block number {end}");
        }

        long diff = endHeader.Number - beginHeader.Number;
        if (diff < 0)
        {
            return ResultWrapper<ulong[]>.Fail("illegal begin and end block number, begin > end");
        }
        if (diff > 50_000)
        {
            return ResultWrapper<ulong[]>.Fail("block range over limit of 50,000 blocks");
        }

        if (beginHeader is not XdcBlockHeader xdcBeginHeader || endHeader is not XdcBlockHeader xdcEndHeader)
        {
            return ResultWrapper<ulong[]>.Fail("Headers are not XDC block headers");
        }

        EpochSwitchInfo[] epochSwitchInfos = epochSwitchManager.GetEpochSwitchInfoBetween(xdcBeginHeader, xdcEndHeader);
        if (epochSwitchInfos is null)
        {
            return ResultWrapper<ulong[]>.Fail("Failed to get epoch switch info");
        }

        ulong[] epochSwitchNumbers = new ulong[epochSwitchInfos.Length];
        for (int i = 0; i < epochSwitchInfos.Length; i++)
        {
            epochSwitchNumbers[i] = (ulong)epochSwitchInfos[i].EpochSwitchBlockInfo.BlockNumber;
        }

        return ResultWrapper<ulong[]>.Success(epochSwitchNumbers);
    }

    private static IDictionary<(ulong Round, Hash256 Hash), SignerTypes> CalculateSigners<T>(
        IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, T>> pool,
        Address[] masternodes)
    {
        Dictionary<(ulong Round, Hash256 Hash), SignerTypes> message = [];

        foreach (((ulong, Hash256) key, Dictionary<Address, T> objs) in pool)
        {
            List<Address> currentSigners = [];
            HashSet<Address> missingSigners = [.. masternodes];

            int num = objs.Count;
            foreach (Address signer in objs.Keys)
            {
                currentSigners.Add(signer);
                missingSigners.Remove(signer);
            }

            message[key] = new SignerTypes
            {
                CurrentNumber = num,
                CurrentSigners = [.. currentSigners],
                MissingSigners = [.. missingSigners]
            };
        }

        return message;
    }

    public ResultWrapper<PoolStatus> XDPoS_getLatestPoolStatus()
    {
        BlockHeader? header = tree.Head?.Header;
        if (header is null)
        {
            return ResultWrapper<PoolStatus>.Fail("Cannot get current block header");
        }

        if (header is not XdcBlockHeader xdcHeader)
        {
            return ResultWrapper<PoolStatus>.Fail("Current header is not an XDC block header");
        }
        EpochSwitchInfo? epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochSwitchInfo is null)
        {
            return ResultWrapper<PoolStatus>.Fail($"Cannot get epoch switch info for current block {header.Number}");
        }

        Address[] masternodes = epochSwitchInfo.Masternodes;

        IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, Vote>> receivedVotes = voteManager.GetReceivedVotes();
        IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, Timeout>> receivedTimeouts = timeoutCertificateManager.GetReceivedTimeouts();
        IDictionary<(ulong Round, Hash256 Hash), SyncInfoTypes> receivedSyncInfo = syncInfoManager.GetReceivedSyncInfos();

        PoolStatus info = new();

        info.Timeout = CalculateSigners(receivedTimeouts, masternodes);
        info.Vote = CalculateSigners(receivedVotes, masternodes);
        info.SyncInfo = receivedSyncInfo;

        return ResultWrapper<PoolStatus>.Success(info);
    }
    public ResultWrapper<MasternodesStatus> XDPoS_getMasternodesByNumber(BlockParameter blockNumber)
    {
        BlockHeader? header;

        if (blockNumber is null || blockNumber.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockNumber.Type == BlockParameterType.Finalized)
        {
            BlockRoundInfo latestCommittedBlock = quorumCertificateManager.HighestKnownCertificate?.ProposedBlockInfo;
            if (latestCommittedBlock != null)
            {
                header = tree.FindHeader(latestCommittedBlock.Hash);
            }
            else
            {
                return ResultWrapper<MasternodesStatus>.Fail("No finalized block found from consensus");
            }
        }
        else if (blockNumber.BlockNumber < 0)
        {
            return ResultWrapper<MasternodesStatus>.Fail($"Invalid block number {blockNumber.BlockNumber}");
        }
        else
        {
            header = tree.FindHeader(blockNumber.BlockNumber.Value);
        }

        if (header is null)
        {
            return ResultWrapper<MasternodesStatus>.Fail($"can not get header by number {blockNumber.BlockNumber}");
        }

        if (header is not XdcBlockHeader xdcHeader)
        {
            return ResultWrapper<MasternodesStatus>.Fail("Header is not an XDC block header");
        }

        if (xdcHeader.ExtraConsensusData is null)
        {
            return ResultWrapper<MasternodesStatus>.Fail($"Block {header.Number} does not contain consensus data (round information)");
        }

        ulong round = xdcHeader.ExtraConsensusData.BlockRound;
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader);

        ulong epochNum = (ulong)spec.SwitchEpoch + round / (ulong)spec.EpochLength;

        EpochSwitchInfo? epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochSwitchInfo is null)
        {
            return ResultWrapper<MasternodesStatus>.Fail($"Cannot get epoch switch info for block {header.Number}, hash {header.Hash}");
        }

        Address[] masternodes = epochSwitchInfo.Masternodes;
        Address[] penalties = epochSwitchInfo.Penalties;
        Address[] standbynodes = epochSwitchInfo.StandbyNodes;

        MasternodesStatus info = new()
        {
            Epoch = epochNum,
            Number = (ulong)header.Number,
            Round = round,
            MasternodesLen = masternodes.Length,
            Masternodes = masternodes,
            PenaltyLen = penalties.Length,
            Penalty = penalties,
            StandbynodesLen = standbynodes.Length,
            Standbynodes = standbynodes
        };

        return ResultWrapper<MasternodesStatus>.Success(info);
    }

    public ResultWrapper<PublicApiMissedRoundsMetadata> XDPoS_getMissedRoundsInEpochByBlockNum(BlockParameter blockNumber)
    {
        BlockHeader? header;

        if (blockNumber is null || blockNumber.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockNumber.BlockNumber < 0)
        {
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Fail($"Invalid block number {blockNumber.BlockNumber}");
        }
        else
        {
            header = tree.FindHeader(blockNumber.BlockNumber.Value);
        }

        if (header is null)
        {
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Fail("can not get header by number");
        }

        if (header is not XdcBlockHeader xdcHeader)
        {
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Fail("Header is not an XDC block header");
        }

        try
        {
            PublicApiMissedRoundsMetadata result = xdcHeader.CalculateMissingRounds(tree, epochSwitchManager, specProvider);
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Success(result);
        }
        catch (Exception ex)
        {
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Fail(ex.Message);
        }
    }

    public ResultWrapper<AccountRewardResponse> XDPoS_getRewardByAccount(Address account, long begin, long end)
    {
        BlockHeader? beginHeader = tree.FindHeader(begin);
        if (beginHeader is null)
        {
            return ResultWrapper<AccountRewardResponse>.Fail($"illegal begin block number {begin}");
        }

        BlockHeader? endHeader = tree.FindHeader(end);
        if (endHeader is null)
        {
            return ResultWrapper<AccountRewardResponse>.Fail($"illegal end block number {end}");
        }

        long diff = endHeader.Number - beginHeader.Number;
        if (diff < 0)
        {
            return ResultWrapper<AccountRewardResponse>.Fail("illegal begin and end block number, begin > end");
        }
        if (diff > 50_000)
        {
            return ResultWrapper<AccountRewardResponse>.Fail("block range over limit of 50,000 blocks");
        }

        if (beginHeader is not XdcBlockHeader xdcBeginHeader || endHeader is not XdcBlockHeader xdcEndHeader)
        {
            return ResultWrapper<AccountRewardResponse>.Fail("Headers are not XDC block headers");
        }

        EpochSwitchInfo[] epochSwitchInfos = epochSwitchManager.GetEpochSwitchInfoBetween(xdcBeginHeader, xdcEndHeader);
        if (epochSwitchInfos is null)
        {
            return ResultWrapper<AccountRewardResponse>.Fail("Failed to get epoch switch info");
        }

        if (epochSwitchInfos.Length != 0 && rewardsStore.TryGetRetainedRange(out ulong oldestRetainedEpochBlockNumber, out _))
        {
            ulong requestedOldestEpoch = (ulong)epochSwitchInfos[0].EpochSwitchBlockInfo.BlockNumber;
            if (requestedOldestEpoch < oldestRetainedEpochBlockNumber)
            {
                return ResultWrapper<AccountRewardResponse>.Fail(
                    $"Cannot return pruned historical reward data before epoch block {oldestRetainedEpochBlockNumber}.",
                    ErrorCodes.PrunedHistoryUnavailable);
            }
        }

        List<AccountEpochReward> epochRewards = new(epochSwitchInfos.Length);
        UInt256 totalReward = UInt256.Zero;

        // No epoch switches in the requested range means no rewards to aggregate.
        foreach (EpochSwitchInfo epochSwitchInfo in epochSwitchInfos)
        {
            ulong epochBlockNumber = (ulong)epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
            Hash256 epochBlockHash = epochSwitchInfo.EpochSwitchBlockInfo.Hash;
            if (!rewardsStore.HasEpochRewards(epochBlockHash))
            {
                return ResultWrapper<AccountRewardResponse>.Fail($"Reward data not available for epoch block {epochBlockNumber}");
            }

            if (!rewardsStore.TryGetAccountReward(account, epochBlockHash, out UInt256 accountReward))
            {
                continue;
            }

            totalReward += accountReward;
            epochRewards.Add(new AccountEpochReward
            {
                EpochBlockNum = epochBlockNumber,
                Address = account,
                AccountStatus = "owner",
                AccountReward = accountReward,
                DelegatedReward = []
            });
        }

        return ResultWrapper<AccountRewardResponse>.Success(new AccountRewardResponse
        {
            EpochRewards = [.. epochRewards],
            Total = new TotalRewards
            {
                Address = account,
                StartBlockNum = (ulong)begin,
                EndBlockNum = (ulong)end,
                TotalAccountReward = totalReward,
                TotalDelegatedReward = []
            }
        });
    }

    public ResultWrapper<Address[]> XDPoS_getSigners(BlockParameter blockParam)
    {
        BlockHeader header;

        if (blockParam is null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockNumber < 0)
        {
            return ResultWrapper<Address[]>.Fail($"Invalid block number {blockParam.BlockNumber}");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockNumber.Value);
        }

        if (header is null)
        {
            return ResultWrapper<Address[]>.Fail("Unknown block");
        }

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(header.Number);

        if (header.Number < spec.SwitchBlock)
        {
            return ResultWrapper<Address[]>.Fail("Unsupported block version : V1");
        }

        Snapshot? snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        if (snapshot is null)
        {
            return ResultWrapper<Address[]>.Fail($"Snapshot not found for block {header.Number}");
        }

        return ResultWrapper<Address[]>.Success(snapshot.NextEpochCandidates);
    }

    public ResultWrapper<Address[]> XDPoS_getSignersAtHash(BlockParameter blockParam)
    {
        BlockHeader header;
        if (blockParam is null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockHash is null)
        {
            return ResultWrapper<Address[]>.Fail("Block hash must be provided");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockHash);
        }
        if (header is null)
        {
            return ResultWrapper<Address[]>.Fail("Unknown block");
        }
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(header.Number);


        if (header.Number < spec.SwitchBlock)
        {
            return ResultWrapper<Address[]>.Fail("Unsupported block version : V1");
        }

        Snapshot? snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        if (snapshot is null)
        {
            return ResultWrapper<Address[]>.Fail($"Snapshot not found for block {header.Number}");
        }
        return ResultWrapper<Address[]>.Success(snapshot.NextEpochCandidates);
    }

    public ResultWrapper<PublicApiSnapshot> XDPoS_getSnapshot(BlockParameter blockParam)
    {
        BlockHeader header;

        if (blockParam is null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockNumber < 0)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail($"Invalid block number {blockParam.BlockNumber}");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockNumber.Value);
        }

        if (header is null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Unknown block");
        }

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(header.Number);

        if (header.Number < spec.SwitchBlock)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Unsupported block version : V1");
        }

        if (header is not XdcBlockHeader)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Header is not an XDC block header");
        }

        Snapshot? snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        if (snapshot is null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail($"Snapshot not found for block {header.Number}");
        }
        return ResultWrapper<PublicApiSnapshot>.Success(snapshot.BuildRpcSnapshot());
    }

    public ResultWrapper<PublicApiSnapshot> XDPoS_getSnapshotAtHash(BlockParameter blockParam)
    {
        BlockHeader header;
        if (blockParam is null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockHash is null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Block hash must be provided");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockHash);
        }
        if (header is null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Unknown block");
        }
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(header.Number);

        if (header.Number < spec.SwitchBlock)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Unsupported block version : V1");
        }

        if (header is not XdcBlockHeader)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Header is not an XDC block header");
        }

        Snapshot? snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        if (snapshot is null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail($"Snapshot not found for block {header.Number}");
        }
        return ResultWrapper<PublicApiSnapshot>.Success(snapshot.BuildRpcSnapshot());
    }

    public ResultWrapper<V2BlockInfo> XDPoS_getV2BlockByHash(BlockParameter blockParam)
    {
        BlockHeader header;
        if (blockParam is null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockHash is null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Block hash must be provided");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockHash);
        }

        return header is null ?
             ResultWrapper<V2BlockInfo>.Fail("Unknown block") :
            BuildV2BlockInfo(header);
    }

    private ResultWrapper<V2BlockInfo> BuildV2BlockInfo(BlockHeader header)
    {
        if (header is null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Header cannot be null");
        }

        if (header is not XdcBlockHeader xdcHeader)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Header is not an XDC block header");
        }

        bool committed = false;
        BlockRoundInfo latestCommittedBlock = quorumCertificateManager.HighestKnownCertificate?.ProposedBlockInfo;

        if (latestCommittedBlock is null)
        {
            return ResultWrapper<V2BlockInfo>.Success(new V2BlockInfo
            {
                Hash = header.Hash,
                Error = "can not find latest committed block from consensus"
            });
        }

        if (header.Number <= latestCommittedBlock.BlockNumber)
        {
            committed = true;
        }

        // Get round number from extra consensus data
        ulong round = 0;
        if (xdcHeader.ExtraConsensusData != null)
        {
            round = xdcHeader.ExtraConsensusData.BlockRound;
        }
        else
        {
            return ResultWrapper<V2BlockInfo>.Fail($"Block {xdcHeader.Hash} does not contain consensus data (round information)");
        }

        // Encode header to RLP
        XdcHeaderDecoder headerDecoder = new();
        byte[] encodedBytes;
        try
        {
            Rlp encoded = headerDecoder.Encode(xdcHeader);
            encodedBytes = encoded.Bytes;
        }
        catch (Exception ex)
        {
            return ResultWrapper<V2BlockInfo>.Fail(ex.Message);
        }

        // Build and return V2BlockInfo
        return ResultWrapper<V2BlockInfo>.Success(new V2BlockInfo
        {
            Hash = header.Hash,
            ParentHash = header.ParentHash,
            Number = (UInt256)header.Number,
            Round = round,
            Committed = committed,
            Miner = header.Beneficiary,
            Timestamp = header.Timestamp,
            EncodedRLP = Convert.ToBase64String(encodedBytes)
        });
    }

    public ResultWrapper<V2BlockInfo> XDPoS_getV2BlockByNumber(BlockParameter blockNumber)
    {
        BlockHeader header;
        if (blockNumber is null || blockNumber.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockNumber.BlockNumber < 0)
        {
            return ResultWrapper<V2BlockInfo>.Fail($"Invalid block number {blockNumber.BlockNumber}");
        }
        else
        {
            header = tree.FindHeader(blockNumber.BlockNumber.Value);
        }
        return (header is null) ?
            ResultWrapper<V2BlockInfo>.Fail("Unknown block") :
            BuildV2BlockInfo(header);
    }

    public ResultWrapper<NetworkInformation> XDPoS_networkInformation()
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(tree.Head?.Header?.Number ?? 0);

        NetworkInformation info = new()
        {
            NetworkId = (UInt256)specProvider.NetworkId,
            XDCValidatorAddress = spec.MasternodeVotingContract,
            LendingAddress = spec.XDCXLendingAddressBinary,
            XDCXListingAddress = spec.XDCXAddressBinary,
            ConsensusConfigs = new XDPoSConfig
            {
                Epoch = spec.EpochLength,
                Gap = spec.Gap,
                Period = spec.MinePeriod,
                Reward = (int)spec.Reward,
                SwitchEpoch = spec.SwitchEpoch,
                SwitchBlock = spec.SwitchBlock,
                V2Configs = spec.V2Configs
            }
        };

        return ResultWrapper<NetworkInformation>.Success(info);
    }
}
