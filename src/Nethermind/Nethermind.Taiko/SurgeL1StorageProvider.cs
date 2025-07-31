// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Taiko;

/// <summary>
/// Surge-specific L1 storage provider that uses injected data from the execution payload
/// to be used by the L1Sload precompile.
/// </summary>
public class SurgeL1StorageProvider : IL1StorageProvider
{
    private readonly ILogger _logger;
    private Dictionary<string, UInt256>? _currentBlockStorageData;

    public SurgeL1StorageProvider(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<SurgeL1StorageProvider>();
    }

    public void SetBlockStorageData(L1StorageMapping[]? storageMappings)
    {
        if (storageMappings is null || storageMappings.Length == 0)
        {
            _currentBlockStorageData = null;
            return;
        }

        _currentBlockStorageData = new Dictionary<string, UInt256>();
        foreach (L1StorageMapping mapping in storageMappings)
        {
            var key = CreateStorageKey(mapping.ContractAddress, mapping.StorageKey, mapping.BlockNumber);
            _currentBlockStorageData[key] = mapping.StorageValue;
        }

        if (_logger.IsDebug) _logger.Debug($"Set {storageMappings.Length} L1 storage mappings");
    }

    public UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        if (_currentBlockStorageData == null)
        {
            // Missing injected data in the execution payload
            if (_logger.IsError) _logger.Error($"L1SLOAD failed: No storage data injected. contract={contractAddress}, key={storageKey}, block={blockNumber}");
            return null;
        }

        var key = CreateStorageKey(contractAddress, storageKey, blockNumber);
        if (_currentBlockStorageData.TryGetValue(key, out var value))
        {
            if (_logger.IsTrace) _logger.Trace($"L1SLOAD hit: contract={contractAddress}, key={storageKey}, block={blockNumber}, value={value}");
            return value;
        }

        if (_logger.IsError) _logger.Error($"L1SLOAD failed: Storage value not found in injected data. contract={contractAddress}, key={storageKey}, block={blockNumber}");
        return null;
    }

    private static string CreateStorageKey(Address contractAddress, UInt256 storageKey, UInt256 blockNumber)
    {
        return $"{contractAddress}_{storageKey}_{blockNumber}";
    }
}
