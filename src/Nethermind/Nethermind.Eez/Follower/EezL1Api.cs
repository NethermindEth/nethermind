// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Eez.Follower;

/// <summary>Reads L1 over JSON-RPC.</summary>
/// <remarks>Does not own <paramref name="rpcClient"/>; the plugin manages its lifetime.</remarks>
public sealed class EezL1Api(IJsonRpcClient rpcClient) : IEezL1Api
{
    public Task<ulong?> GetChainId() => rpcClient.Post<ulong?>("eth_chainId");

    public Task<EezL1Block?> GetBlockByNumber(ulong number) => rpcClient.Post<EezL1Block?>("eth_getBlockByNumber", number.ToHexString(true), false);

    public Task<EezL1Block?> GetLatestBlock() => rpcClient.Post<EezL1Block?>("eth_getBlockByNumber", "latest", false);

    public Task<EezL1Block?> GetFinalizedBlock() => rpcClient.Post<EezL1Block?>("eth_getBlockByNumber", "finalized", false);

    public Task<EezL1Log[]?> GetLogs(Address address, Hash256 topic0, Hash256? topic1, ulong fromBlock, ulong toBlock) =>
        rpcClient.Post<EezL1Log[]?>("eth_getLogs", new
        {
            address,
            topics = topic1 is null ? new[] { topic0 } : [topic0, topic1],
            fromBlock = fromBlock.ToHexString(true),
            toBlock = toBlock.ToHexString(true),
        });

    public Task<EezL1Transaction?> GetTransactionByBlockHashAndIndex(Hash256 blockHash, ulong index) =>
        rpcClient.Post<EezL1Transaction?>("eth_getTransactionByBlockHashAndIndex", blockHash, index.ToHexString(true));
}
