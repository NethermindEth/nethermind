// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// The masternode and staking views XDPoS adds to the <c>eth</c> namespace.
/// </summary>
[RpcModule(ModuleType.Eth)]
public interface IXdcMasternodeEthRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the masternodes that signed the block identified by hash.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<Address[]> eth_getBlockSignersByHash(Hash256 blockHash);

    [JsonRpcMethod(
        Description = "Returns the masternodes that signed the given block.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<Address[]> eth_getBlockSignersByNumber(BlockParameter? blockParameter = null);

    [JsonRpcMethod(
        Description = "Returns the percentage of masternodes that signed the block identified by hash.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<uint> eth_getBlockFinalityByHash(Hash256 blockHash);

    [JsonRpcMethod(
        Description = "Returns the percentage of masternodes that signed the given block.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<uint> eth_getBlockFinalityByNumber(BlockParameter? blockParameter = null);

    [JsonRpcMethod(
        Description = "Returns the status and stake of every masternode candidate in the given epoch.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<XdcCandidatesResult> eth_getCandidates(XdcEpochParameter? epoch = null);

    [JsonRpcMethod(
        Description = "Returns the status and stake of one masternode candidate in the given epoch.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<XdcCandidateStatusResult> eth_getCandidateStatus(Address coinbase, XdcEpochParameter? epoch = null);

    [JsonRpcMethod(
        Description = "Estimates the annual return, in percent, for staking on the network.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<double> eth_getStakerROI();

    [JsonRpcMethod(
        Description = "Estimates the annual return, in percent, for staking on a specific masternode.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<double> eth_getStakerROIMasternode(Address masternode);

    [JsonRpcMethod(
        Description = "Returns the minted and burned token totals as of the given epoch.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<XdcTokenSupply> eth_getTokenStats(XdcEpochParameter? epoch = null);
}
