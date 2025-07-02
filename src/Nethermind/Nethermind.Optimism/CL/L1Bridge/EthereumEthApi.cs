// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL.L1Bridge;

public class EthereumEthApi(string l1EthApiEndpoint, IJsonSerializer jsonSerializer, ILogManager logManager) : IEthApi
{
    private readonly IJsonRpcClient _ethRpcClient = new BasicJsonRpcClient(new Uri(l1EthApiEndpoint), jsonSerializer, logManager);

    public Task<ReceiptForRpc[]?> GetReceiptsByHash(Hash256 blockHash)
    {
        return _ethRpcClient.Post<ReceiptForRpc[]>("eth_getBlockReceipts", new object[] { blockHash });
    }

    public Task<L1Block?> GetBlockByHash(Hash256 blockHash, bool fullTxs)
    {
        return _ethRpcClient.Post<L1Block?>("eth_getBlockByHash", new object[] { blockHash, fullTxs });
    }

    public Task<L1Block?> GetBlockByNumber(ulong blockNumber, bool fullTxs)
    {
        return _ethRpcClient.Post<L1Block?>("eth_getBlockByNumber", new BlockParameter((long)blockNumber), fullTxs);
    }

    public Task<L1Block?> GetHead(bool fullTxs)
    {
        return _ethRpcClient.Post<L1Block?>("eth_getBlockByNumber", BlockParameter.Latest, fullTxs);
    }

    public Task<L1Block?> GetFinalized(bool fullTxs)
    {
        return _ethRpcClient.Post<L1Block?>("eth_getBlockByNumber", BlockParameter.Finalized, fullTxs);
    }

    public Task<L1Block?> GetSafe(bool fullTxs)
    {
        return _ethRpcClient.Post<L1Block?>("eth_getBlockByNumber", BlockParameter.Safe, fullTxs);
    }

    public async Task<ulong> GetChainId()
    {
        return await _ethRpcClient.Post<ulong?>("eth_chainId") ?? throw new NullReferenceException();
    }
}
