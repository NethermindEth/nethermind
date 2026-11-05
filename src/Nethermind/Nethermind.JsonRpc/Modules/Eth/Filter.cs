// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
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
                    : BlockParameter.Latest;
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
    // LOG4 is the widest log, so a 5th slot can never match; the log index also has one db per slot.
    private const int MaxTopicSlots = 4;

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
                int index = 0;
                foreach (JsonElement element in token.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        throw new SafePublicMessageFormatException($"non-string address at index {index}");
                    }

                    result.Add(new(ParseAddress(element, index++)));
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
        if (topicSlotCount > MaxTopicSlots)
        {
            throw new JsonException($"Too many topics ({topicSlotCount}). Max is {MaxTopicSlots}.");
        }

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
                        result[i++] = element.ValueKind switch
                        {
                            JsonValueKind.String => ParseHash(element),
                            JsonValueKind.Null => throw new ArgumentException("null topic in a topic list"),
                            _ => throw new SafePublicMessageFormatException("invalid topic(s)")
                        };
                    }

                    topics[slotIndex++] = result;
                    break;
                default:
                    throw new ArgumentException("invalid topics field");
            }
        }

        return topics;
    }

    private static Address ParseAddress(JsonElement element, int index = -1)
    {
        Span<byte> bytes = stackalloc byte[Core.Address.Size];
        if (TryDecodeHex(element, bytes))
        {
            return new Address(bytes);
        }

        int length = DecodeHex(element, bytes);
        return length == Core.Address.Size
            ? new Address(bytes)
            : throw new SafePublicMessageFormatException(index < 0
                ? $"invalid address: {GetInvalidLengthMessage(length, Core.Address.Size, "address")}"
                : $"invalid address at index {index}: {GetInvalidLengthMessage(length, Core.Address.Size, "address")}");
    }

    private static Hash256 ParseHash(JsonElement element)
    {
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        if (TryDecodeHex(element, bytes))
        {
            return new Hash256(bytes);
        }

        int length = DecodeHex(element, bytes);
        return length == Hash256.Size
            ? new Hash256(bytes)
            : throw new SafePublicMessageFormatException(GetInvalidLengthMessage(length, Hash256.Size, "topic"));
    }

    private static int DecodeHex(JsonElement element, Span<byte> destination)
    {
        string hex = element.ToString();
        int digits = hex.StartsWith("0x", StringComparison.Ordinal) ? hex.Length - 2 : hex.Length;
        if ((digits + 1) / 2 == destination.Length)
        {
            Bytes.FromHexString(hex.AsSpan(), destination);
            return destination.Length;
        }

        return Bytes.FromHexString(hex).Length;
    }

    private static string GetInvalidLengthMessage(int length, int expectedLength, string name) =>
        $"hex has invalid length {length} after decoding; expected {expectedLength} for {name}";

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
