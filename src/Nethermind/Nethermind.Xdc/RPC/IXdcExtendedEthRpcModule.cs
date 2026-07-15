// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Xdc.RPC;

[RpcModule(ModuleType.Eth)]
public interface IXdcExtendedEthRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the masternode owner for a coinbase address at the given block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<Address>> eth_getOwnerByCoinbase(Address coinbase, BlockParameter? blockParameter = null);

    [JsonRpcMethod(
        Description = "Returns epoch reward distribution for the block identified by hash.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<XdcEpochRewards>> eth_getRewardByHash(
        Hash256 blockHash);

    [JsonRpcMethod(
        Description = "Returns Merkle proofs for a transaction and its receipt.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<XdcTransactionAndReceiptProof?>> eth_getTransactionAndReceiptProof(Hash256 transactionHash);
}
