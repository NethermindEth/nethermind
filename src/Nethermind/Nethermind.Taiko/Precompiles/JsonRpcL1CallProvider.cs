// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Does not own <paramref name="rpcClient"/> — caller (TaikoPlugin) manages its lifetime.
/// </summary>
public class JsonRpcL1CallProvider(IJsonRpcClient rpcClient, ILogManager logManager) : IL1CallProvider
{
    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcL1CallProvider>();

    public byte[]? ExecuteStaticCall(Address contractAddress, UInt256 blockNumber, byte[] calldata)
    {
        try
        {
            string calldataHex = calldata.ToHexString(withZeroX: true);
            if (_logger.IsDebug) _logger.Debug($"L1STATICCALL: sending eth_call — contract={contractAddress}, calldata_len={calldata.Length}, block={blockNumber.ToHexString(true)}");

            // Sync-over-async: IPrecompile.Run() is synchronous by design.
            // Acceptable for devnet; async precompile pipeline needed for production load.
            string? response = rpcClient.Post<string>("eth_call", new object[]
            {
                new { to = contractAddress.ToString(), data = calldataHex },
                blockNumber.ToHexString(true)
            }).GetAwaiter().GetResult();

            if (response is null)
            {
                if (_logger.IsWarn) _logger.Warn($"L1STATICCALL: eth_call returned null — contract={contractAddress}, block={blockNumber.ToHexString(true)}");
                return null;
            }

            byte[] result = Convert.FromHexString(response.StartsWith("0x") ? response[2..] : response);
            if (_logger.IsDebug) _logger.Debug($"L1STATICCALL: eth_call success — contract={contractAddress}, block={blockNumber.ToHexString(true)}, return_len={result.Length}");
            return result;
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"L1STATICCALL: eth_call exception — contract={contractAddress}, block={blockNumber.ToHexString(true)}, error={ex}");
            return null;
        }
    }

}
