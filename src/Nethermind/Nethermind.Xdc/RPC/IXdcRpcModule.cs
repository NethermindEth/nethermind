// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Xdc.Spec;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Xdc;

public interface IXdcRpcModule : IRpcModule
{
    /// <summary>
    /// Retrieves the state snapshot at a given block number
    /// </summary>
    ResultWrapper<PublicApiSnapshot> GetSnapshot(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the state snapshot at a given block hash
    /// </summary>
    ResultWrapper<PublicApiSnapshot> GetSnapshotAtHash(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the list of authorized signers at the specified block
    /// </summary>
    ResultWrapper<Address[]> GetSigners(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the list of authorized signers at the specified block hash
    /// </summary>
    ResultWrapper<Address[]> GetSignersAtHash(BlockParameter blockParam);

    /// <summary>
    /// Gets masternode information by block number
    /// </summary>
    ResultWrapper<MasternodesStatus> GetMasternodesByNumber(BlockParameter blockNumber);

    /// <summary>
    /// Gets the current vote pool and timeout pool content and missing messages
    /// </summary>
    ResultWrapper<PoolStatus> GetLatestPoolStatus();

    /// <summary>
    /// Gets V2 block information by header
    /// </summary>
    ResultWrapper<V2BlockInfo> GetV2BlockByHeader(BlockHeader header, bool uncle);

    /// <summary>
    /// Gets V2 block information by block number
    /// </summary>
    ResultWrapper<V2BlockInfo> GetV2BlockByNumber(BlockParameter blockNumber);

    /// <summary>
    /// Confirms V2 block committed status by hash
    /// </summary>
    ResultWrapper<V2BlockInfo> GetV2BlockByHash(BlockParameter blockParam);

    /// <summary>
    /// Gets network configuration information
    /// </summary>
    ResultWrapper<NetworkInformation> NetworkInformation();

    /// <summary>
    /// Gets missed rounds in epoch by block number (V2 consensus only)
    /// </summary>
    ResultWrapper<PublicApiMissedRoundsMetadata> GetMissedRoundsInEpochByBlockNum(BlockParameter blockNumber);

    /// <summary>
    /// Gets reward information for a specific account between block numbers
    /// </summary>
    ResultWrapper<AccountRewardResponse> GetRewardByAccount(Address account, long begin, long end);

    /// <summary>
    /// Gets epoch numbers between two block numbers
    /// </summary>
    ResultWrapper<ulong[]> GetEpochNumbersBetween(long begin, long end);

    /// <summary>
    /// Gets block information by V2 epoch number
    /// </summary>
    ResultWrapper<EpochNumInfo> GetBlockInfoByV2EpochNum(ulong epochNumber);

    /// <summary>
    /// Calculates block information by V1 epoch number
    /// </summary>
    ResultWrapper<EpochNumInfo> CalculateBlockInfoByV1EpochNum(ulong targetEpochNum);

    /// <summary>
    /// Gets block information by epoch number (supports both V1 and V2)
    /// </summary>
    ResultWrapper<EpochNumInfo> GetBlockInfoByEpochNum(ulong epochNumber);
}

public class MissedRoundInfo
{
    public ulong Round { get; set; }
    public Address? Miner { get; set; }
    public Hash256? CurrentBlockHash { get; set; }
    public UInt256? CurrentBlockNum { get; set; }
    public Hash256? ParentBlockHash { get; set; }
    public UInt256? ParentBlockNum { get; set; }
}

public class PublicApiMissedRoundsMetadata
{
    public ulong EpochRound { get; set; }
    public UInt256? EpochBlockNumber { get; set; }
    public MissedRoundInfo[]? MissedRounds { get; set; }
}

public class XDPoSConfig
{
    public int Epoch { get; set; }
    public int Gap { get; set; }
    public int Period { get; set; }
    public int Reward { get; set; }
    public int SwitchEpoch { get; set; }
    public long SwitchBlock { get; set; }
    public List<V2ConfigParams>? V2Configs { get; set; }
}
