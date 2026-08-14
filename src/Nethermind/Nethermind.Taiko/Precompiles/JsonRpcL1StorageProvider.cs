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
public class JsonRpcL1StorageProvider(IJsonRpcClient rpcClient, ILogManager logManager) : IL1StorageProvider
{
    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcL1StorageProvider>();

    public UInt256? GetStorageValue(Address contractAddress, UInt256 blockNumber, UInt256 storageKey)
    {
        try
        {
            if (_logger.IsDebug) _logger.Debug($"L1SLOAD: sending eth_getStorageAt — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}");

            // Sync-over-async: IPrecompile.Run() is synchronous by design.
            // Acceptable for devnet; async precompile pipeline needed for production load.
            string? response = rpcClient.Post<string>("eth_getStorageAt", new object[]
            {
                contractAddress.ToString(),
                storageKey.ToHexString(true),
                blockNumber.ToHexString(true)
            }).GetAwaiter().GetResult();

            if (response is null)
            {
                if (_logger.IsWarn) _logger.Warn($"L1SLOAD: eth_getStorageAt returned null — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}");
                return null;
            }

            UInt256 parsedValue = UInt256.Parse(response);
            if (_logger.IsDebug) _logger.Debug($"L1SLOAD: eth_getStorageAt success — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}, value={parsedValue}");
            return parsedValue;
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"L1SLOAD: eth_getStorageAt exception — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}, error={ex}");
            return null;
        }
    }

}
