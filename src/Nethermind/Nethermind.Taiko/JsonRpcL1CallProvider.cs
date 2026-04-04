// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Taiko;

/// <summary>
/// JsonRpc-based implementation of IL1CallProvider that uses eth_call
/// RPC to execute read-only calls against L1 contracts.
/// </summary>
public class JsonRpcL1CallProvider : IL1CallProvider
{
    private readonly IJsonRpcClient _rpcClient;
    private readonly ILogger _logger;

    public JsonRpcL1CallProvider(string l1EthApiEndpoint, IJsonSerializer jsonSerializer, ILogManager logManager)
    {
        _rpcClient = new BasicJsonRpcClient(new Uri(l1EthApiEndpoint), jsonSerializer, logManager, L1PrecompileConstants.L1RpcTimeout);
        _logger = logManager.GetClassLogger<JsonRpcL1CallProvider>();
    }

    public byte[]? ExecuteStaticCall(Address target, UInt256 blockNumber, byte[] calldata)
    {
        try
        {
            string calldataHex = "0x" + Convert.ToHexString(calldata).ToLowerInvariant();
            if (_logger.IsDebug) _logger.Debug($"L1STATICCALL: sending eth_call — target={target}, calldata_len={calldata.Length}, block={blockNumber.ToHexString(true)}");

            string? response = _rpcClient.Post<string>("eth_call", new object[]
            {
                new { to = target.ToString(), data = calldataHex },
                blockNumber.ToHexString(true)
            }).GetAwaiter().GetResult();

            if (response == null)
            {
                if (_logger.IsWarn) _logger.Warn($"L1STATICCALL: eth_call returned null — target={target}, block={blockNumber.ToHexString(true)}");
                return null;
            }

            byte[] result = Convert.FromHexString(response.StartsWith("0x") ? response[2..] : response);
            if (_logger.IsDebug) _logger.Debug($"L1STATICCALL: eth_call success — target={target}, block={blockNumber.ToHexString(true)}, return_len={result.Length}");
            return result;
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"L1STATICCALL: eth_call exception — target={target}, block={blockNumber}, error={ex}");
            return null;
        }
    }
}
