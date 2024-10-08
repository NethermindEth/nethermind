// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.CL;

public class EthereumEthApi : IEthApi
{
    private readonly IJsonRpcClient _ethRpcClient;

    public EthereumEthApi(ICLConfig config, IJsonSerializer jsonSerializer, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(config.L1EthApiEndpoint);
        _ethRpcClient = new BasicJsonRpcClient(new Uri(config.L1EthApiEndpoint), jsonSerializer, logManager);
    }

    public Task<BlockForRpc?> GetBlockByNumber(ulong blockNumber)
    {
        return _ethRpcClient.Post<BlockForRpc>("eth_getBlockByNumber", new object[] { blockNumber, true });
    }
}
