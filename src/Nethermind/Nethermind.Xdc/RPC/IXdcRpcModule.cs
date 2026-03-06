// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Xdc.RPC;
using System.Numerics;

namespace Nethermind.Xdc;

[RpcModule(ModuleTypeExt.Xdc)]
public interface IXdcRpcModule : IRpcModule
{
    /// <summary>
    /// Retrieves the state snapshot at a given block number
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the state snapshot at a given block number")]
    ResultWrapper<PublicApiSnapshot> GetSnapshot(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the state snapshot at a given block hash
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the state snapshot at a given block hash")]
    ResultWrapper<PublicApiSnapshot> GetSnapshotAtHash(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the list of authorized signers at the specified block
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block")]
    ResultWrapper<Address[]> GetSigners(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the list of authorized signers at the specified block hash
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block hash")]
    ResultWrapper<Address[]> GetSignersAtHash(BlockParameter blockParam);

    /// <summary>
    /// Gets masternode information by block number
    /// </summary>
    [JsonRpcMethod(Description = "Gets masternode information by block number")]
    ResultWrapper<MasternodesStatus> GetMasternodesByNumber(BlockParameter blockNumber);

    /// <summary>
    /// Gets the current vote pool and timeout pool content and missing messages
    /// </summary>
    [JsonRpcMethod(Description = "Gets the current vote pool and timeout pool content and missing messages")]
    ResultWrapper<PoolStatus> GetLatestPoolStatus();

    /// <summary>
    /// Gets V2 block information by header
    /// </summary>
    [JsonRpcMethod(Description = "Gets V2 block information by header")]
    ResultWrapper<V2BlockInfo> GetV2BlockByHeader(BlockHeader header, bool uncle);

    /// <summary>
    /// Gets V2 block information by block number
    /// </summary>
    [JsonRpcMethod(Description = "Gets V2 block information by block number")]
    ResultWrapper<V2BlockInfo> GetV2BlockByNumber(BlockParameter blockNumber);

    /// <summary>
    /// Confirms V2 block committed status by hash
    /// </summary>
    [JsonRpcMethod(Description = "Confirms V2 block committed status by hash")]
    ResultWrapper<V2BlockInfo> GetV2BlockByHash(BlockParameter blockParam);

    /// <summary>
    /// Gets network configuration information
    /// </summary>
    [JsonRpcMethod(Description = "Gets network configuration information")]
    ResultWrapper<NetworkInformation> NetworkInformation();

    /// <summary>
    /// Gets missed rounds in epoch by block number (V2 consensus only)
    /// </summary>
    [JsonRpcMethod(Description = "Gets missed rounds in epoch by block number (V2 consensus only)")]
    ResultWrapper<PublicApiMissedRoundsMetadata> GetMissedRoundsInEpochByBlockNum(BlockParameter blockNumber);

    /// <summary>
    /// Gets reward information for a specific account between block numbers
    /// </summary>
    [JsonRpcMethod(Description = "Gets reward information for a specific account between block numbers")]
    ResultWrapper<AccountRewardResponse> GetRewardByAccount(Address account, long begin, long end);

    /// <summary>
    /// Gets epoch numbers between two block numbers
    /// </summary>
    [JsonRpcMethod(Description = "Gets epoch numbers between two block numbers")]
    ResultWrapper<ulong[]> GetEpochNumbersBetween(long begin, long end);

    /// <summary>
    /// Gets block information by V2 epoch number
    /// </summary>
    [JsonRpcMethod(Description = "Gets block information by V2 epoch number")]
    ResultWrapper<EpochNumInfo> GetBlockInfoByV2EpochNum(ulong epochNumber);

    /// <summary>
    /// Calculates block information by V1 epoch number
    /// </summary>
    [JsonRpcMethod(Description = "Calculates block information by V1 epoch number")]
    ResultWrapper<EpochNumInfo> CalculateBlockInfoByV1EpochNum(ulong targetEpochNum);

    /// <summary>
    /// Gets block information by epoch number (supports both V1 and V2)
    /// </summary>
    [JsonRpcMethod(Description = "Gets block information by epoch number (supports both V1 and V2)")]
    ResultWrapper<EpochNumInfo> GetBlockInfoByEpochNum(ulong epochNumber);
}
