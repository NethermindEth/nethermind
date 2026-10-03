// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth;

public class Filter : IJsonRpcParam
{
    public HashSet<AddressAsKey>? Address { get; set; }

    public BlockParameter FromBlock { get; set; } = BlockParameter.Latest;

    public BlockParameter ToBlock { get; set; } = BlockParameter.Latest;

    public IEnumerable<Hash256[]?>? Topics { get; set; }

    public bool UseIndex { get; set; } = true;

    public void ReadJson(JsonElement filter, JsonSerializerOptions options)
    {
        JsonDocument doc = null;
        try
        {
            if (filter.ValueKind == JsonValueKind.String)
            {
                string filterString = filter.GetString()!;
                if (filterString.Length > JsonRpcLimits.MaxJsonStringArgLength)
                {
                    throw new ArgumentException($"filter string length {filterString.Length} exceeds maximum allowed length of {JsonRpcLimits.MaxJsonStringArgLength}");
                }

                doc = JsonDocument.Parse(filterString);
                filter = doc.RootElement;
            }

            bool hasBlockHash = filter.TryGetProperty("blockHash"u8, out JsonElement blockHashElement);
            bool hasFromBlock = filter.TryGetProperty("fromBlock"u8, out JsonElement fromBlockElement);
            bool hasToBlock = filter.TryGetProperty("toBlock"u8, out JsonElement toBlockElement);

            if (hasBlockHash && blockHashElement.ValueKind != JsonValueKind.Null)
            {
                if (hasFromBlock || hasToBlock)
                {
                    throw new ArgumentException(ErrorMessages.BlockHashAndRange);
                }

                FromBlock = new(new Hash256(blockHashElement.ToString()));
                ToBlock = FromBlock;
            }
            else
            {
                FromBlock = hasFromBlock && fromBlockElement.ValueKind != JsonValueKind.Null
                    ? BlockParameterConverter.GetBlockParameter(fromBlockElement.ToString())
                    : BlockParameter.Earliest;
                ToBlock = hasToBlock && toBlockElement.ValueKind != JsonValueKind.Null
                    ? BlockParameterConverter.GetBlockParameter(toBlockElement.ToString())
                    : BlockParameter.Latest;
            }

            if (filter.TryGetProperty("address"u8, out JsonElement addressElement))
            {
                Address = GetAddress(addressElement, options);
            }

            if (filter.TryGetProperty("topics"u8, out JsonElement topicsElement) && topicsElement.ValueKind == JsonValueKind.Array)
            {
                Topics = GetTopics(topicsElement, options);
            }

            if (filter.TryGetProperty("useIndex"u8, out JsonElement useIndex))
            {
                UseIndex = useIndex.ValueKind switch
                {
                    JsonValueKind.False => false,
                    JsonValueKind.True => true,
                    _ => UseIndex
                };
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private const int MaxAddressCount = 1000;
    private const int MaxTopicValuesPerSlot = 1000;

    private static HashSet<AddressAsKey>? GetAddress(JsonElement token, JsonSerializerOptions options)
    {
        switch (token.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return [new AddressAsKey(ParseAddress(token))];
            case JsonValueKind.Array:
                int addressCount = token.GetArrayLength();
                if (addressCount > MaxAddressCount)
                {
                    throw new JsonException($"Too many addresses ({addressCount}). Max is {MaxAddressCount}.");
                }

                HashSet<AddressAsKey> result = new(addressCount);
                foreach (JsonElement element in token.EnumerateArray())
                {
                    result.Add(new(ParseAddress(element)));
                }

                return result;
            default:
                throw new ArgumentException("invalid address field");
        }
    }

    private static Hash256[]?[]? GetTopics(JsonElement? array, JsonSerializerOptions options)
    {
        if (array is null)
        {
            return null;
        }

        int topicSlotCount = array.Value.GetArrayLength();
        Hash256[]?[] topics = new Hash256[]?[topicSlotCount];
        int slotIndex = 0;
        foreach (JsonElement token in array.Value.EnumerateArray())
        {
            switch (token.ValueKind)
            {
                case JsonValueKind.Undefined or JsonValueKind.Null:
                    topics[slotIndex++] = null;
                    break;
                case JsonValueKind.String:
                    topics[slotIndex++] = [ParseHash(token)];
                    break;
                case JsonValueKind.Array:
                    int topicCount = token.GetArrayLength();
                    if (topicCount > MaxTopicValuesPerSlot)
                    {
                        throw new JsonException($"Too many topic values ({topicCount}). Max is {MaxTopicValuesPerSlot}.");
                    }

                    Hash256[] result = new Hash256[topicCount];
                    int i = 0;
                    foreach (JsonElement element in token.EnumerateArray())
                    {
                        result[i++] = ParseHash(element);
                    }

                    topics[slotIndex++] = result;
                    break;
                default:
                    throw new ArgumentException("invalid topics field");
            }
        }

        return topics;
    }

    private static Address ParseAddress(JsonElement element)
    {
        Span<byte> bytes = stackalloc byte[Core.Address.Size];
        return TryDecodeHex(element, bytes) ? new Address(bytes) : new Address(element.ToString());
    }

    private static Hash256 ParseHash(JsonElement element)
    {
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        return TryDecodeHex(element, bytes) ? new Hash256(bytes) : new Hash256(element.ToString());
    }

    /// <summary>
    /// Decodes a JSON string of exactly <paramref name="destination"/>'s length in even-length hex straight from its UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Anything else (escapes, odd length, wrong size, non-strings) returns <see langword="false"/> so callers fall back to the
    /// string parse, which keeps the accepted inputs and the errors unchanged.
    /// </remarks>
    private static bool TryDecodeHex(JsonElement element, Span<byte> destination)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        ReadOnlySpan<byte> hex = JsonMarshal.GetRawUtf8Value(element)[1..^1];
        if (hex is [(byte)'0', (byte)'x', ..])
        {
            hex = hex[2..];
        }

        return hex.Length == destination.Length * 2 && HexConverter.TryDecodeFromUtf8(hex, destination);
    }
}
