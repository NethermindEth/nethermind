// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly HashSet<AddressAsKey>? _restrictedAddresses;

    public JsonRpcL1StorageProvider(string l1EthApiEndpoint, IJsonSerializer jsonSerializer, ILogManager logManager, HashSet<AddressAsKey>? restrictedAddresses = null)
    {
        _rpcClient = new BasicJsonRpcClient(new Uri(l1EthApiEndpoint), jsonSerializer, logManager);
        _logger = logManager.GetClassLogger<JsonRpcL1StorageProvider>();
        _restrictedAddresses = restrictedAddresses;
    }

    public UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        // Check if the address is restricted
        if (_restrictedAddresses?.Contains(contractAddress) == true)
        {
            if (_logger.IsDebug) _logger.Debug($"L1SLOAD blocked: Restricted address {contractAddress}");
            return null;
        }

        try
        {
            var response = _rpcClient.Post<string>("eth_getStorageAt", new object[]
            {
                contractAddress.ToString(),
                storageKey.ToBigEndian().ToHexString(true),
                blockNumber.ToString()
            }).GetAwaiter().GetResult();

            if (response == null)
            {
                _logger.Warn($"Failed to read L1 storage: contract={contractAddress}, key={storageKey}, block={blockNumber}");
                return null;
            }

            // Parse hex response
            string hexValue = response.StartsWith("0x") ? response[2..] : response;
            return UInt256.Parse(hexValue, NumberStyles.HexNumber);
        }
        catch (Exception ex)
        {
            _logger.Error($"L1 storage read failed: {ex.Message}");
            return null;
        }
    }
}
