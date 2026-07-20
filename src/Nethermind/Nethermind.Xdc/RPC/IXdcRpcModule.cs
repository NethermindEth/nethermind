// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Xdc.RPC;

[RpcModule(ModuleTypeExt.Xdc)]
public interface IXdcRpcModule : IRpcModule
{
    /// <summary>
    /// Retrieves the state snapshot at a given block number
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the state snapshot at a given block number")]
    ResultWrapper<PublicApiSnapshot> XDPoS_getSnapshot(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the state snapshot at a given block hash
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the state snapshot at a given block hash")]
    ResultWrapper<PublicApiSnapshot> XDPoS_getSnapshotAtHash(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the list of authorized signers at the specified block
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block")]
    ResultWrapper<Address[]> XDPoS_getSigners(BlockParameter blockParam);

    /// <summary>
    /// Retrieves the list of authorized signers at the specified block hash
    /// </summary>
    [JsonRpcMethod(Description = "Retrieves the list of authorized signers at the specified block hash")]
    ResultWrapper<Address[]> XDPoS_getSignersAtHash(BlockParameter blockParam);

    /// <summary>
    /// Gets masternode information by block number
    /// </summary>
    [JsonRpcMethod(Description = "Gets masternode information by block number")]
    ResultWrapper<MasternodesStatus> XDPoS_getMasternodesByNumber(BlockParameter blockNumber);

    /// <summary>
    /// Gets the current vote pool and timeout pool content and missing messages
    /// </summary>
    [JsonRpcMethod(Description = "Gets the current vote pool and timeout pool content and missing messages")]
    ResultWrapper<PoolStatus> XDPoS_getLatestPoolStatus();

    /// <summary>
    /// Gets V2 block information by block number
    /// </summary>
    [JsonRpcMethod(Description = "Gets V2 block information by block number")]
    ResultWrapper<V2BlockInfo> XDPoS_getV2BlockByNumber(BlockParameter blockNumber);

    /// <summary>
    /// Confirms V2 block committed status by hash
    /// </summary>
    [JsonRpcMethod(Description = "Confirms V2 block committed status by hash")]
    ResultWrapper<V2BlockInfo> XDPoS_getV2BlockByHash(BlockParameter blockParam);

    /// <summary>
    /// Gets network configuration information
    /// </summary>
    [JsonRpcMethod(Description = "Gets network configuration information")]
    ResultWrapper<NetworkInformation> XDPoS_networkInformation();

    /// <summary>
    /// Gets missed rounds in epoch by block number (V2 consensus only)
    /// </summary>
    [JsonRpcMethod(Description = "Gets missed rounds in epoch by block number (V2 consensus only)")]
    ResultWrapper<PublicApiMissedRoundsMetadata> XDPoS_getMissedRoundsInEpochByBlockNum(BlockParameter blockNumber);

    /// <summary>
    /// Gets reward information for a specific account between block numbers
    /// </summary>
    [JsonRpcMethod(Description = "Gets reward information for a specific account between block numbers")]
    ResultWrapper<AccountRewardResponse> XDPoS_getRewardByAccount(Address account, ulong begin, ulong end);

    /// <summary>
    /// Gets epoch numbers between two block numbers
    /// </summary>
    [JsonRpcMethod(Description = "Gets epoch numbers between two block numbers")]
    ResultWrapper<ulong[]> XDPoS_getEpochNumbersBetween(ulong begin, ulong end);

    /// <summary>
    /// Gets block information by V2 epoch number
    /// </summary>
    [JsonRpcMethod(Description = "Gets block information by V2 epoch number")]
    ResultWrapper<EpochNumInfo> XDPoS_getBlockInfoByV2EpochNum(ulong epochNumber);

    /// <summary>
    /// Calculates block information by V1 epoch number
    /// </summary>
    [JsonRpcMethod(Description = "Calculates block information by V1 epoch number")]
    ResultWrapper<EpochNumInfo> XDPoS_calculateBlockInfoByV1EpochNum(ulong targetEpochNum);

    /// <summary>
    /// Gets block information by epoch number (supports both V1 and V2)
    /// </summary>
    [JsonRpcMethod(Description = "Gets block information by epoch number (supports both V1 and V2)")]
    ResultWrapper<EpochNumInfo> XDPoS_getBlockInfoByEpochNum(ulong epochNumber);
}
