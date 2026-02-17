// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.RPC;

internal class XdcRpcModule(IBlockTree tree, ISnapshotManager snapshotManager, ISpecProvider specProvider, IQuorumCertificateManager quorumCertificateManager, IEpochSwitchManager epochSwitchManager, IVotesManager voteManager, ITimeoutCertificateManager timeoutCertificateManager, ISyncInfoManager syncInfoManager) : IXdcRpcModule
{
    public ResultWrapper<EpochNumInfo> CalculateBlockInfoByV1EpochNum(ulong targetEpochNum)
    {
        throw new NotSupportedException("Calculating block info by V1 epoch number is not supported because only XDC V2 is supported");
    }

    public ResultWrapper<EpochNumInfo> GetBlockInfoByEpochNum(ulong epochNumber)
    {
        var spec = specProvider.GetXdcSpec(tree.Head?.Header?.Number ?? 0);
        
        if (epochNumber < (ulong)spec.SwitchEpoch)
        {
            return CalculateBlockInfoByV1EpochNum(epochNumber);
        }
        
        return GetBlockInfoByV2EpochNum(epochNumber);
    }

    public ResultWrapper<EpochNumInfo> GetBlockInfoByV2EpochNum(ulong epochNumber)
    {
        BlockRoundInfo? thisEpoch = epochSwitchManager.GetBlockByEpochNumber(epochNumber);
        if (thisEpoch == null)
        {
            return ResultWrapper<EpochNumInfo>.Fail($"Cannot find epoch {epochNumber}");
        }

        EpochNumInfo info = new EpochNumInfo
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

    public ResultWrapper<ulong[]> GetEpochNumbersBetween(long begin, long end)
    {
        BlockHeader beginHeader = tree.FindHeader(begin);
        if (beginHeader == null)
        {
            return ResultWrapper<ulong[]>.Fail($"illegal begin block number {begin}");
        }

        BlockHeader endHeader = tree.FindHeader(end);
        if (endHeader == null)
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

        XdcBlockHeader xdcBeginHeader = beginHeader as XdcBlockHeader;
        XdcBlockHeader xdcEndHeader = endHeader as XdcBlockHeader;

        if (xdcBeginHeader == null || xdcEndHeader == null)
        {
            return ResultWrapper<ulong[]>.Fail("Headers are not XDC block headers");
        }

        EpochSwitchInfo[] epochSwitchInfos = epochSwitchManager.GetEpochSwitchInfoBetween(xdcBeginHeader, xdcEndHeader);
        if (epochSwitchInfos == null)
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
        IDictionary<(ulong Round, Hash256 Hash), ArrayPoolList<T>> pool,
        Address[] masternodes,
        Func<T, Address?> getSignerFunc)
    {
        var message = new Dictionary<(ulong Round, Hash256 Hash), SignerTypes>();

        foreach (var (key, objs) in pool)
        {
            List<Address> currentSigners = new List<Address>();
            List<Address> missingSigners = new List<Address>(masternodes);

            int num = objs.Count;
            foreach (var obj in objs)
            {
                Address? signer = getSignerFunc(obj);
                if (signer != null)
                {
                    currentSigners.Add(signer);
                    missingSigners.Remove(signer);
                }
            }

            message[key] = new SignerTypes
            {
                CurrentNumber = num,
                CurrentSigners = currentSigners.ToArray(),
                MissingSigners = missingSigners.ToArray()
            };
        }

        return message;
    }

    public ResultWrapper<PoolStatus> GetLatestPoolStatus()
    {
        BlockHeader? header = tree.Head?.Header;
        if (header == null)
        {
            return ResultWrapper<PoolStatus>.Fail("Cannot get current block header");
        }

        XdcBlockHeader? xdcHeader = header as XdcBlockHeader;
        if (xdcHeader == null)
        {
            return ResultWrapper<PoolStatus>.Fail("Current header is not an XDC block header");
        }

        EpochSwitchInfo? epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochSwitchInfo == null)
        {
            return ResultWrapper<PoolStatus>.Fail($"Cannot get epoch switch info for current block {header.Number}");
        }

        Address[] masternodes = epochSwitchInfo.Masternodes;

        var receivedVotes = voteManager.GetReceivedVotes();
        var receivedTimeouts = timeoutCertificateManager.GetReceivedTimeouts();
        var receivedSyncInfo = syncInfoManager.GetReceivedSyncInfos();

        PoolStatus info = new PoolStatus();

        info.Timeout = CalculateSigners(receivedTimeouts, masternodes, timeout => timeout.Signer);
        info.Vote = CalculateSigners(receivedVotes, masternodes, vote => vote.Signer);

        foreach (var (name, objList) in receivedSyncInfo)
        {
            foreach (var syncInfo in objList)
            {
                (ulong round, Hash256 hash) key = syncInfo.PoolKey();

                int qcSigners = syncInfo.HighestQuorumCert?.Signatures?.Length ?? 0;
                int tcSigners = 0;
                if (syncInfo.HighestTimeoutCert != null)
                {
                    tcSigners = syncInfo.HighestTimeoutCert.Signatures?.Length ?? 0;
                }

                info.SyncInfo[key] = new SyncInfoTypes
                {
                    Hash = key.hash,
                    QCSigners = qcSigners,
                    TCSigners = tcSigners
                };
            }
        }

        return ResultWrapper<PoolStatus>.Success(info);
    }
    public ResultWrapper<MasternodesStatus> GetMasternodesByNumber(BlockParameter blockNumber)
    {
        BlockHeader? header;

        if (blockNumber == null || blockNumber.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockNumber.Type == BlockParameterType.Finalized)
        {
            var latestCommittedBlock = quorumCertificateManager.HighestKnownCertificate?.ProposedBlockInfo;
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

        if (header == null)
        {
            return ResultWrapper<MasternodesStatus>.Fail($"can not get header by number {blockNumber.BlockNumber}");
        }

        XdcBlockHeader? xdcHeader = header as XdcBlockHeader;
        if (xdcHeader == null)
        {
            return ResultWrapper<MasternodesStatus>.Fail("Header is not an XDC block header");
        }

        if (xdcHeader.ExtraConsensusData == null)
        {

            return ResultWrapper<MasternodesStatus>.Fail($"Block {header.Number} does not contain consensus data (round information)");
        }

        ulong round = xdcHeader.ExtraConsensusData.BlockRound;
        var spec = specProvider.GetXdcSpec(xdcHeader);

        ulong epochNum = (ulong)spec.SwitchEpoch + round / (ulong)spec.EpochLength;

        EpochSwitchInfo? epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(xdcHeader);
        if (epochSwitchInfo == null)
        {
            return ResultWrapper<MasternodesStatus>.Fail($"Cannot get epoch switch info for block {header.Number}, hash {header.Hash}");
        }

        Address[] masternodes = epochSwitchInfo.Masternodes;
        Address[] penalties = epochSwitchInfo.Penalties;
        Address[] standbynodes = epochSwitchInfo.StandbyNodes;

        MasternodesStatus info = new MasternodesStatus
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

    public ResultWrapper<PublicApiMissedRoundsMetadata> GetMissedRoundsInEpochByBlockNum(BlockParameter blockNumber)
    {
        BlockHeader? header;
        
        if (blockNumber == null || blockNumber.Type == BlockParameterType.Latest)
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

        if (header == null)
        {
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Fail("can not get header by number");
        }

        XdcBlockHeader? xdcHeader = header as XdcBlockHeader;
        if (xdcHeader == null)
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
            return ResultWrapper<PublicApiMissedRoundsMetadata>.Fail(ex);
        }
    }

    public ResultWrapper<AccountRewardResponse> GetRewardByAccount(Address account, long begin, long end)
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<Address[]> GetSigners(BlockParameter blockParam)
    {
        BlockHeader header;

        if (blockParam == null || blockParam.Type == BlockParameterType.Latest)
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

        if (header == null)
        {
            return ResultWrapper<Address[]>.Fail("Unknown block");
        }

        var spec = specProvider.GetXdcSpec(header.Number);
        var snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);

        return ResultWrapper<Address[]>.Success(snapshot.GetSigners());
    }

    public ResultWrapper<Address[]> GetSignersAtHash(BlockParameter blockParam)
    {
        BlockHeader header;
        if (blockParam == null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockHash == null)
        {
            return ResultWrapper<Address[]>.Fail("Block hash must be provided");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockHash);
        }
        if (header == null)
        {
            return ResultWrapper<Address[]>.Fail("Unknown block");
        }
        var spec = specProvider.GetXdcSpec(header.Number);
        var snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        return ResultWrapper<Address[]>.Success(snapshot.GetSigners());
    }

    public ResultWrapper<PublicApiSnapshot> GetSnapshot(BlockParameter blockParam)
    {
        BlockHeader header;

        if (blockParam == null || blockParam.Type == BlockParameterType.Latest)
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

        if (header == null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Unknown block");
        }

        var spec = specProvider.GetXdcSpec(header.Number);

        var snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        return ResultWrapper<PublicApiSnapshot>.Success(snapshot.BuildRpcSnapshot((XdcBlockHeader)header));
    }

    public ResultWrapper<PublicApiSnapshot> GetSnapshotAtHash(BlockParameter blockParam)
    {
        BlockHeader header;
        if (blockParam == null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockHash == null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Block hash must be provided");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockHash);
        }
        if (header == null)
        {
            return ResultWrapper<PublicApiSnapshot>.Fail("Unknown block");
        }
        var spec = specProvider.GetXdcSpec(header.Number);
        var snapshot = snapshotManager.GetSnapshotByBlockNumber(header.Number, spec);
        return ResultWrapper<PublicApiSnapshot>.Success(snapshot.BuildRpcSnapshot((XdcBlockHeader)header));
    }

    public ResultWrapper<V2BlockInfo> GetV2BlockByHash(BlockParameter blockParam)
    {
        BlockHeader header;
        if (blockParam == null || blockParam.Type == BlockParameterType.Latest)
        {
            header = tree.Head?.Header;
        }
        else if (blockParam.BlockHash == null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Block hash must be provided");
        }
        else
        {
            header = tree.FindHeader(blockParam.BlockHash);
        }
        if (header == null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Unknown block");
        }
        return GetV2BlockByHeader(header, uncle: false);
    }

    public ResultWrapper<V2BlockInfo> GetV2BlockByHeader(BlockHeader header, bool uncle)
    {
        if (header == null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Header cannot be null");
        }

        XdcBlockHeader xdcHeader = header as XdcBlockHeader;
        if (xdcHeader == null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Header is not an XDC block header");
        }

        bool committed = false;
        var latestCommittedBlock = quorumCertificateManager.HighestKnownCertificate?.ProposedBlockInfo;
        
        if (latestCommittedBlock == null)
        {
            return ResultWrapper<V2BlockInfo>.Success(new V2BlockInfo
            {
                Hash = header.Hash,
                Error = "can not find latest committed block from consensus"
            });
        }
        
        if (header.Number <= latestCommittedBlock.BlockNumber)
        {
            committed = true && !uncle;
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
        XdcHeaderDecoder headerDecoder = new XdcHeaderDecoder();
        byte[] encodedBytes;
        try
        {
            Rlp encoded = headerDecoder.Encode(xdcHeader);
            encodedBytes = encoded.Bytes;
        }
        catch (Exception ex)
        {
            return ResultWrapper<V2BlockInfo>.Fail(ex);
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

    public ResultWrapper<V2BlockInfo> GetV2BlockByNumber(BlockParameter blockNumber)
    {
        BlockHeader header;
        if (blockNumber == null || blockNumber.Type == BlockParameterType.Latest)
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
        if (header == null)
        {
            return ResultWrapper<V2BlockInfo>.Fail("Unknown block");
        }
        return GetV2BlockByHeader(header, uncle: false);
    }

    public ResultWrapper<NetworkInformation> NetworkInformation()
    {
        var spec = specProvider.GetXdcSpec(tree.Head?.Header?.Number ?? 0);
        
        NetworkInformation info = new NetworkInformation
        {
            NetworkId = (UInt256)specProvider.NetworkId,
            XDCValidatorAddress = spec.MasternodeVotingContract,
            LendingAddress = spec.XDCXLendingAddressBinary,
            RelayerRegistrationAddress = spec.RelayerRegistrationSMC,
            XDCXListingAddress = spec.XDCXAddressBinary,
            XDCZAddress = spec.TRC21IssuerSMC,
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
