// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Strict parsers for untrusted MCP tool arguments.</summary>
/// <remarks>
/// Every parser returns <see langword="false"/> with a client-safe message naming the offending parameter instead of
/// throwing, so malformed input never reaches the RPC module.
/// </remarks>
internal static class McpToolInput
{
    /// <summary>The maximum number of addresses accepted by <c>get_logs</c>.</summary>
    public const int MaxLogAddresses = 32;

    /// <summary>The maximum number of topic positions accepted by <c>get_logs</c>.</summary>
    public const int MaxTopicPositions = 4;

    /// <summary>The maximum number of alternative hashes accepted at a single <c>get_logs</c> topic position.</summary>
    public const int MaxTopicAlternatives = 32;

    private const int AddressHexLength = 2 + 2 * Address.Size;
    private const int HashHexLength = 2 + 2 * Hash256.Size;
    private const int MaxULongHexDigits = 16;
    private const int MaxULongDecimalDigits = 20;
    private const int MaxUInt256HexDigits = 64;
    private const int MaxUInt256DecimalDigits = 78;

    public static bool TryParseAddress(string? input, string parameter, [NotNullWhen(true)] out Address? address, [NotNullWhen(false)] out string? error)
    {
        address = null;
        if (input is null || input.Length != AddressHexLength || !HasHexPrefix(input) || !IsHexDigits(input.AsSpan(2)))
        {
            error = $"'{parameter}' must be a 20-byte address: 0x followed by 40 hex characters.";
            return false;
        }

        address = new Address(Convert.FromHexString(input.AsSpan(2)));
        error = null;
        return true;
    }

    public static bool TryParseHash(string? input, string parameter, [NotNullWhen(true)] out Hash256? hash, [NotNullWhen(false)] out string? error)
    {
        hash = null;
        if (!IsHash(input))
        {
            error = $"'{parameter}' must be a 32-byte hash: 0x followed by 64 hex characters.";
            return false;
        }

        hash = new Hash256(Convert.FromHexString(input.AsSpan(2)));
        error = null;
        return true;
    }

    /// <summary>Parses a block selector: a tag, a hex or decimal block number, or a block hash.</summary>
    /// <remarks><c>pending</c> is rejected because it has no stable meaning for a read-only tool.</remarks>
    public static bool TryParseBlock(string? input, string parameter, [NotNullWhen(true)] out BlockParameter? block, [NotNullWhen(false)] out string? error)
    {
        block = input switch
        {
            null => null,
            _ when input.Equals("latest", StringComparison.OrdinalIgnoreCase) => BlockParameter.Latest,
            _ when input.Equals("earliest", StringComparison.OrdinalIgnoreCase) => BlockParameter.Earliest,
            _ when input.Equals("safe", StringComparison.OrdinalIgnoreCase) => BlockParameter.Safe,
            _ when input.Equals("finalized", StringComparison.OrdinalIgnoreCase) => BlockParameter.Finalized,
            _ when IsHash(input) => new BlockParameter(new Hash256(Convert.FromHexString(input.AsSpan(2)))),
            _ when TryParseULong(input, out ulong number) => new BlockParameter(number),
            _ => null
        };

        if (block is null)
        {
            error = input is not null && input.Equals("pending", StringComparison.OrdinalIgnoreCase)
                ? $"'{parameter}': \"pending\" is not supported; use \"latest\"."
                : $"'{parameter}' must be \"latest\", \"earliest\", \"safe\", \"finalized\", a non-negative block number (0x-hex or decimal) or a 32-byte block hash.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Parses a non-negative 256-bit quantity given as 0x-hex or decimal.</summary>
    public static bool TryParseUInt256(string? input, string parameter, out UInt256 value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        bool parsed = false;
        if (input is not null && HasHexPrefix(input))
        {
            ReadOnlySpan<char> digits = input.AsSpan(2);
            if (digits.Length > 0 && IsHexDigits(digits))
            {
                digits = digits.TrimStart('0');
                if (digits.Length <= MaxUInt256HexDigits)
                {
                    string evenDigits = digits.Length % 2 == 0 ? digits.ToString() : string.Concat("0", digits);
                    Span<byte> bytes = stackalloc byte[32];
                    Bytes.FromHexString(evenDigits, bytes[(32 - evenDigits.Length / 2)..]);
                    value = new UInt256(bytes, isBigEndian: true);
                    parsed = true;
                }
            }
        }
        else if (input is { Length: > 0 and <= MaxUInt256DecimalDigits } && IsDecimalDigits(input))
        {
            parsed = UInt256.TryParse(input, out value);
        }

        if (!parsed)
        {
            error = $"'{parameter}' must be a non-negative integer up to 2^256-1, as 0x-hex or decimal.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Parses a non-negative 64-bit quantity given as 0x-hex or decimal.</summary>
    public static bool TryParseULong(string? input, string parameter, out ulong value, [NotNullWhen(false)] out string? error)
    {
        if (!TryParseULong(input, out value))
        {
            error = $"'{parameter}' must be a non-negative integer up to 2^64-1, as 0x-hex or decimal.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Parses 0x-prefixed, even-length hex data of at most <paramref name="maxBytes"/> bytes.</summary>
    public static bool TryParseData(string? input, string parameter, int maxBytes, [NotNullWhen(true)] out byte[]? data, [NotNullWhen(false)] out string? error)
    {
        data = null;
        if (input is null || !HasHexPrefix(input) || input.Length % 2 != 0 || !IsHexDigits(input.AsSpan(2)))
        {
            error = $"'{parameter}' must be 0x followed by an even number of hex characters (\"0x\" for empty data).";
            return false;
        }

        if ((input.Length - 2) / 2 > maxBytes)
        {
            error = $"'{parameter}' is {(input.Length - 2) / 2} bytes; the maximum is {maxBytes} bytes.";
            return false;
        }

        data = Convert.FromHexString(input.AsSpan(2));
        error = null;
        return true;
    }

    /// <summary>Parses the optional <c>get_logs</c> address list; an empty list matches any address.</summary>
    public static bool TryParseAddresses(string[]? input, string parameter, out HashSet<AddressAsKey>? addresses, [NotNullWhen(false)] out string? error)
    {
        addresses = null;
        if (input is null || input.Length == 0)
        {
            error = null;
            return true;
        }

        if (input.Length > MaxLogAddresses)
        {
            error = $"'{parameter}' has {input.Length} entries; the maximum is {MaxLogAddresses}.";
            return false;
        }

        HashSet<AddressAsKey> result = new(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (!TryParseAddress(input[i], $"{parameter}[{i}]", out Address? address, out error))
            {
                return false;
            }

            result.Add(address);
        }

        addresses = result;
        error = null;
        return true;
    }

    /// <summary>
    /// Parses <c>get_logs</c> topics: up to four positions, each <see langword="null"/> (any), a hash, or an array of
    /// hashes (any of them). An empty array at a position also matches any topic.
    /// </summary>
    public static bool TryParseTopics(JsonElement[]? input, string parameter, out Hash256[]?[]? topics, [NotNullWhen(false)] out string? error)
    {
        topics = null;
        if (input is null || input.Length == 0)
        {
            error = null;
            return true;
        }

        if (input.Length > MaxTopicPositions)
        {
            error = $"'{parameter}' has {input.Length} positions; the maximum is {MaxTopicPositions}.";
            return false;
        }

        Hash256[]?[] result = new Hash256[]?[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            JsonElement position = input[i];
            switch (position.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    break;
                case JsonValueKind.String:
                    if (!TryParseHash(position.GetString(), $"{parameter}[{i}]", out Hash256? topic, out error))
                    {
                        return false;
                    }

                    result[i] = [topic];
                    break;
                case JsonValueKind.Array:
                    int count = position.GetArrayLength();
                    if (count > MaxTopicAlternatives)
                    {
                        error = $"'{parameter}[{i}]' has {count} alternatives; the maximum is {MaxTopicAlternatives}.";
                        return false;
                    }

                    if (count == 0)
                    {
                        break;
                    }

                    Hash256[] alternatives = new Hash256[count];
                    int j = 0;
                    foreach (JsonElement alternative in position.EnumerateArray())
                    {
                        string? text = alternative.ValueKind == JsonValueKind.String ? alternative.GetString() : null;
                        if (!TryParseHash(text, $"{parameter}[{i}][{j}]", out Hash256? hash, out error))
                        {
                            return false;
                        }

                        alternatives[j++] = hash;
                    }

                    result[i] = alternatives;
                    break;
                default:
                    error = $"'{parameter}[{i}]' must be null, a 32-byte hash, or an array of 32-byte hashes.";
                    return false;
            }
        }

        topics = result;
        error = null;
        return true;
    }

    /// <summary>Parses a storage slot: a 0x-hex value of up to 32 bytes (such as a keccak-derived slot) or a decimal slot index.</summary>
    public static bool TryParseStorageSlot(string? input, string parameter, out UInt256 slot, [NotNullWhen(false)] out string? error)
    {
        if (!TryParseUInt256(input, parameter, out slot, out _))
        {
            error = $"'{parameter}' must be a storage slot: 0x followed by up to 64 hex characters (for example a 32-byte keccak slot), or a decimal slot index such as \"0\".";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Parses a list of at most <paramref name="maxKeys"/> storage slots; duplicates are allowed and collapse.</summary>
    public static bool TryParseStorageSlots(string[]? input, string parameter, int maxKeys, [NotNullWhen(true)] out UInt256[]? slots, [NotNullWhen(false)] out string? error)
    {
        slots = null;
        input ??= [];
        if (input.Length > maxKeys)
        {
            error = $"'{parameter}' has {input.Length} entries; the maximum is {maxKeys}.";
            return false;
        }

        UInt256[] result = new UInt256[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            if (!TryParseStorageSlot(input[i], $"{parameter}[{i}]", out result[i], out error))
            {
                return false;
            }
        }

        slots = result;
        error = null;
        return true;
    }

    /// <summary>Parses fee-history reward percentiles: 1 to <paramref name="maxCount"/> values in [0, 100], in ascending order.</summary>
    public static bool TryParsePercentiles(double[]? input, string parameter, int maxCount, [NotNullWhen(true)] out double[]? percentiles, [NotNullWhen(false)] out string? error)
    {
        percentiles = null;
        if (input is null || input.Length == 0 || input.Length > maxCount)
        {
            error = $"'{parameter}' must have between 1 and {maxCount} values.";
            return false;
        }

        for (int i = 0; i < input.Length; i++)
        {
            double value = input[i];
            if (!double.IsFinite(value) || value < 0 || value > 100 || (i > 0 && value < input[i - 1]))
            {
                error = $"'{parameter}' values must be numbers between 0 and 100 in ascending order, such as [10, 50, 90].";
                return false;
            }
        }

        percentiles = input;
        error = null;
        return true;
    }

    private static bool TryParseULong(string? input, out ulong value)
    {
        value = 0;
        if (input is null)
        {
            return false;
        }

        if (HasHexPrefix(input))
        {
            ReadOnlySpan<char> digits = input.AsSpan(2);
            if (digits.Length == 0 || !IsHexDigits(digits))
            {
                return false;
            }

            digits = digits.TrimStart('0');
            return digits.Length == 0
                || (digits.Length <= MaxULongHexDigits && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value));
        }

        return input.Length is > 0 and <= MaxULongDecimalDigits
            && IsDecimalDigits(input)
            && ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsHash([NotNullWhen(true)] string? input) =>
        input is { Length: HashHexLength } && HasHexPrefix(input) && IsHexDigits(input.AsSpan(2));

    private static bool HasHexPrefix(string input) => input is ['0', 'x' or 'X', ..];

    private static bool IsHexDigits(ReadOnlySpan<char> input)
    {
        foreach (char c in input)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalDigits(ReadOnlySpan<char> input)
    {
        foreach (char c in input)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
