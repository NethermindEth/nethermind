// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Avalanche.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Avalanche.Sync;

/// <summary>
/// Reconstructs an <see cref="AvalancheBlockHeader"/> from a Coreth <c>eth_getBlockByNumber</c> JSON result
/// object, mapping every header field (including the Avalanche/Granite extras) so that
/// <see cref="AvalancheHeaderDecoder.ComputeHash"/> over the result reproduces the block's network hash.
/// </summary>
/// <remarks>
/// Coreth's <c>eth_getBlockByNumber</c> response omits header optionals that are absent from the wire RLP
/// (e.g. blob/beacon fields pre-Cancun, <c>timestampMilliseconds</c>/<c>minDelayExcess</c> pre-Granite), so a
/// field is set on the header iff it is present in the JSON — faithfully reproducing the
/// <c>rlp:"optional"</c> presence across fork eras. The JSON exposes <c>TimeMilliseconds</c> under the name
/// <c>timestampMilliseconds</c>.
/// </remarks>
public static class AvalancheRpcHeaderMapper
{
    /// <summary>Builds the header from a block result object. Does not set <see cref="BlockHeader.Hash"/>.</summary>
    public static AvalancheBlockHeader MapHeader(JsonElement block)
    {
        AvalancheBlockHeader header = new(
            new Hash256(GetString(block, "parentHash")),
            new Hash256(GetString(block, "sha3Uncles")),
            new Address(GetString(block, "miner")),
            difficulty: (UInt256)ToULong(GetString(block, "difficulty")),
            number: ToULong(GetString(block, "number")),
            gasLimit: ToULong(GetString(block, "gasLimit")),
            timestamp: ToULong(GetString(block, "timestamp")),
            extraData: Bytes.FromHexString(GetString(block, "extraData")))
        {
            StateRoot = new Hash256(GetString(block, "stateRoot")),
            TxRoot = new Hash256(GetString(block, "transactionsRoot")),
            ReceiptsRoot = new Hash256(GetString(block, "receiptsRoot")),
            Bloom = new Bloom(Bytes.FromHexString(GetString(block, "logsBloom"))),
            GasUsed = ToULong(GetString(block, "gasUsed")),
            MixHash = new Hash256(GetString(block, "mixHash")),
            Nonce = ToULong(GetString(block, "nonce")),
            ExtDataHash = new Hash256(GetString(block, "extDataHash"))
        };

        // Trailing rlp:"optional" fields: present in the JSON iff present in the wire RLP.
        if (TryGetString(block, "baseFeePerGas", out string? baseFee)) header.BaseFeePerGas = (UInt256)ToULong(baseFee!);
        if (TryGetString(block, "extDataGasUsed", out string? extGas)) header.ExtDataGasUsed = (UInt256)ToULong(extGas!);
        if (TryGetString(block, "blockGasCost", out string? blockGasCost)) header.BlockGasCost = (UInt256)ToULong(blockGasCost!);
        if (TryGetString(block, "blobGasUsed", out string? blobGasUsed)) header.BlobGasUsed = ToULong(blobGasUsed!);
        if (TryGetString(block, "excessBlobGas", out string? excessBlobGas)) header.ExcessBlobGas = ToULong(excessBlobGas!);
        if (TryGetString(block, "parentBeaconBlockRoot", out string? beacon)) header.ParentBeaconBlockRoot = new Hash256(beacon!);
        if (TryGetString(block, "timestampMilliseconds", out string? timeMs)) header.TimeMilliseconds = ToULong(timeMs!);
        if (TryGetString(block, "minDelayExcess", out string? minDelay)) header.MinDelayExcess = ToULong(minDelay!);

        return header;
    }

    /// <summary>The network-reported block hash from the <c>hash</c> field.</summary>
    public static Hash256 ReportedHash(JsonElement block) => new(GetString(block, "hash"));

    private static string GetString(JsonElement obj, string name) =>
        obj.GetProperty(name).GetString() ?? throw new InvalidOperationException($"missing field {name}");

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        if (obj.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static ulong ToULong(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.StartsWith("0x")) span = span[2..];
        return span.IsEmpty ? 0UL : Convert.ToUInt64(span.ToString(), 16);
    }
}
