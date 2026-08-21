// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using Nethermind.JsonRpc;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// An XDPoS epoch number RPC parameter.
/// </summary>
/// <remarks>
/// Accepts a JSON number, a decimal or <c>0x</c>-prefixed string, or the keyword <c>"latest"</c>.
/// Omitting the parameter is equivalent to <c>"latest"</c>, as is any negative number: the reference
/// client's <c>rpc.EpochNumber</c> is a signed integer whose <c>latest</c> sentinel is <c>-1</c>.
/// </remarks>
public sealed class XdcEpochParameter : IJsonRpcParam
{
    public const string LatestKeyword = "latest";

    /// <summary>The requested epoch, or <see langword="null"/> for the epoch containing the current head.</summary>
    public ulong? EpochNumber { get; private set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        switch (jsonValue.ValueKind)
        {
            case JsonValueKind.Null:
                EpochNumber = null;
                break;
            case JsonValueKind.Number:
                EpochNumber = jsonValue.TryGetUInt64(out ulong epochNumber) ? epochNumber
                    : jsonValue.TryGetInt64(out long sentinel) && sentinel < 0 ? null
                    : throw new JsonException($"Cannot parse '{jsonValue.GetRawText()}' as an epoch number.");
                break;
            case JsonValueKind.String:
                EpochNumber = ParseEpoch(jsonValue.GetString()!);
                break;
            default:
                throw new JsonException($"Cannot parse {jsonValue.ValueKind} as an epoch number.");
        }
    }

    private static ulong? ParseEpoch(string value)
    {
        if (value.Length == 0
            || value[0] == '-'
            || string.Equals(value, LatestKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong epoch)
            : ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch);

        return parsed ? epoch : throw new JsonException($"Cannot parse '{value}' as an epoch number.");
    }
}
