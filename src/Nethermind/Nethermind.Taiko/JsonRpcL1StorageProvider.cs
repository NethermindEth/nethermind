// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
/// RPC to retrieve L1 storage values. It uses the anchor block ID as the block
/// number for eth_getStorageAt.
/// </summary>
public class JsonRpcL1StorageProvider : IL1StorageProvider
{
    private static UInt256? _anchorBlockId;
    private readonly IJsonRpcClient _rpcClient;
    private readonly ILogger _logger;

    public JsonRpcL1StorageProvider(
        string l1EthApiEndpoint,
        IJsonSerializer jsonSerializer,
        ILogManager logManager)
    {
        _rpcClient = new BasicJsonRpcClient(new Uri(l1EthApiEndpoint), jsonSerializer, logManager);
        _logger = logManager.GetClassLogger<JsonRpcL1StorageProvider>();
    }

    /// <summary>
    /// Sets the block ID from the anchor transaction.
    /// </summary>
    public static void SetAnchorBlockId(Transaction anchorTx)
    {
        _anchorBlockId = ExtractAnchorBlockId(anchorTx);
    }

    public UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey)
    {
        if (_anchorBlockId is null)
        {
            _logger.Warn($"Anchor block ID not set for L1 storage read: contract={contractAddress}, key={storageKey}");
            return null;
        }

        try
        {
            string? response = _rpcClient.Post<string>("eth_getStorageAt", new object[]
            {
                contractAddress.ToString(),
                storageKey.ToHexString(true),
                _anchorBlockId.Value.ToHexString(true)
            }).GetAwaiter().GetResult();

            if (response == null)
            {
                _logger.Warn(
                    $"Failed to read L1 storage: contract={contractAddress}, " +
                    $"key={storageKey}, anchorBlockId={_anchorBlockId.Value}");
                return null;
            }

            return UInt256.Parse(response);
        }
        catch (Exception ex)
        {
            _logger.Error($"L1 storage read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts the anchor block ID from the anchor transaction data.
    /// </summary>
    private static ulong? ExtractAnchorBlockId(Transaction anchorTx)
    {
        const int SelectorBytes = 4;
        const int SlotBytes = 32;
        const int Uint64Bytes = 8;
        const int Uint64Padding = SlotBytes - Uint64Bytes; // 24 bytes

        if (anchorTx.Data.Length < SelectorBytes + SlotBytes)
            return null;

        ReadOnlySpan<byte> data = anchorTx.Data.Span;
        ReadOnlySpan<byte> selector = data[..SelectorBytes];

        int offset;

        if (selector.SequenceEqual(TaikoBlockValidator.AnchorV2Selector) ||
            selector.SequenceEqual(TaikoBlockValidator.AnchorV3Selector))
        {
            // anchorV2/V3: uint64 _anchorBlockId is parameter[0]
            offset = SelectorBytes + Uint64Padding;
        }
        else if (selector.SequenceEqual(TaikoBlockValidator.AnchorSelector) &&
            data.Length >= SelectorBytes + 3 * SlotBytes)
        {
            // anchor: uint64 _l1BlockId is parameter[2]
            offset = SelectorBytes + (2 * SlotBytes) + Uint64Padding;
        }
        else
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, Uint64Bytes));
    }
}
