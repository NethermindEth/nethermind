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
    /// <summary>Returns the masternodes that signed the block identified by hash.</summary>
    /// <remarks>
    /// "Signed" means a masternode posted a block-signer transaction covering the block. Masternodes only sign
    /// heights that are a multiple of the merge-sign range, so a block is represented by the next such height and
    /// the two share a signer set. Blocks too close to the head represent themselves, because that height does
    /// not exist yet, and report no signers until the transactions land.
    /// </remarks>
    [JsonRpcMethod(
        Description = "Returns the masternodes that signed the block identified by hash.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<Address[]> eth_getBlockSignersByHash(Hash256 blockHash);

    /// <summary>Returns the masternodes that signed the given block.</summary>
    /// <inheritdoc cref="eth_getBlockSignersByHash" path="/remarks"/>
    [JsonRpcMethod(
        Description = "Returns the masternodes that signed the given block.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<Address[]> eth_getBlockSignersByNumber(BlockParameter? blockParameter = null);

    /// <summary>Returns the percentage of masternodes that signed the block identified by hash.</summary>
    /// <remarks>
    /// This is not XDPoS 2.0 finality, despite the name. It measures block-signer transactions, which under V2
    /// feed reward accounting rather than consensus, and which the reference client still serves here as the
    /// pre-2.0 finality proxy. Because those transactions cover the representative block and land after it, a
    /// block that is already committed under the V2 commit rule can report 0%. Read the <c>Committed</c> flag
    /// from <c>XDPoS_getV2BlockByHash</c> or <c>XDPoS_getV2BlockByNumber</c> for actual V2 finality.
    /// </remarks>
    [JsonRpcMethod(
        Description = "Returns the percentage of masternodes that signed the block identified by hash. Not V2 finality; see XDPoS_getV2BlockByHash for that.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<uint> eth_getBlockFinalityByHash(Hash256 blockHash);

    /// <summary>Returns the percentage of masternodes that signed the given block.</summary>
    /// <inheritdoc cref="eth_getBlockFinalityByHash" path="/remarks"/>
    [JsonRpcMethod(
        Description = "Returns the percentage of masternodes that signed the given block. Not V2 finality; see XDPoS_getV2BlockByNumber for that.",
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
