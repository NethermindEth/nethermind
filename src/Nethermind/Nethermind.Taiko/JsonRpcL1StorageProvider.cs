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
/// JsonRpc-based implementation of IL1StorageProvider that uses eth_getStorageAt
/// RPC to retrieve L1 storage values.
/// </summary>
public class JsonRpcL1StorageProvider : IL1StorageProvider
{
    private readonly IJsonRpcClient _rpcClient;
    private readonly ILogger _logger;

    public JsonRpcL1StorageProvider(string l1EthApiEndpoint, IJsonSerializer jsonSerializer, ILogManager logManager)
    {
        _rpcClient = new BasicJsonRpcClient(new Uri(l1EthApiEndpoint), jsonSerializer, logManager);
        _logger = logManager.GetClassLogger<JsonRpcL1StorageProvider>();
    }

    public UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        try
        {
            if (_logger.IsDebug) _logger.Debug($"L1SLOAD: sending eth_getStorageAt — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}");

            string? response = _rpcClient.Post<string>("eth_getStorageAt", new object[]
            {
                contractAddress.ToString(),
                storageKey.ToHexString(true),
                blockNumber.ToHexString(true)
            }).GetAwaiter().GetResult();

            if (response == null)
            {
                if (_logger.IsWarn) _logger.Warn($"L1SLOAD: eth_getStorageAt returned null — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}");
                return null;
            }

            var parsedValue = UInt256.Parse(response);
            if (_logger.IsDebug) _logger.Debug($"L1SLOAD: eth_getStorageAt success — contract={contractAddress}, key={storageKey.ToHexString(true)}, block={blockNumber.ToHexString(true)}, value={parsedValue}");
            return parsedValue;
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"L1SLOAD: eth_getStorageAt exception — contract={contractAddress}, key={storageKey}, block={blockNumber}, error={ex}");
            return null;
        }
    }
}
