// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Taiko;

/// <summary>
/// JsonRpc-based implementation of IL1CallProvider that uses eth_call
/// RPC to execute calls on L1 contracts.
/// </summary>
public class JsonRpcL1CallProvider : IL1CallProvider
{
    private readonly IJsonRpcClient _rpcClient;
    private readonly ILogger _logger;

    public JsonRpcL1CallProvider(string l1EthApiEndpoint, IJsonSerializer jsonSerializer, ILogManager logManager)
    {
        _rpcClient = new BasicJsonRpcClient(new Uri(l1EthApiEndpoint), jsonSerializer, logManager);
        _logger = logManager.GetClassLogger<JsonRpcL1CallProvider>();
    }

    public byte[]? ExecuteCall(Address contractAddress, ulong gas, UInt256 value, byte[]? callData, UInt256 feePerGas)
    {
        try
        {
            string callDataHex = callData == null || callData.Length == 0 ? "0x" : callData.ToHexString(true);
            string? valueHex = value.IsZero ? null : value.ToHexString(true);

            string? response = _rpcClient.Post<string>("eth_call", new
            {
                to = contractAddress.ToString(),
                data = callDataHex,
                value = valueHex,
                gas = gas == 0 ? null : $"0x{gas:x}"
            }, "latest").GetAwaiter().GetResult();

            if (response == null)
            {
                _logger.Warn($"L1 call failed: contract={contractAddress}, gas={gas}, value={value}");
                return null;
            }

            // Parse the hex response
            return Bytes.FromHexString(response);
        }
        catch (Exception ex)
        {
            _logger.Error($"L1 call execution failed: {ex.Message}");
            return null;
        }
    }
}

