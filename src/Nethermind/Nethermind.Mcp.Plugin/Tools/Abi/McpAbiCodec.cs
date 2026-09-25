// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools.Abi;

/// <summary>
/// ABI encoding of JSON arguments and decoding of return data and logs into JSON-friendly values, for signatures parsed
/// by <see cref="McpAbiSignature"/>.
/// </summary>
/// <remarks>
/// <para>Decoded values: <c>address</c> is an EIP-55 checksummed string, integers are decimal strings (exact at any width),
/// <c>bool</c> is a bool, <c>bytes</c>/<c>bytesN</c> are 0x hex strings, <c>string</c> is a string, and arrays and tuples are
/// <see cref="List{T}"/> of values.</para>
/// <para>Decoding is strict (dirty padding, out-of-range offsets and non-canonical bools are rejected) and bounded: it never
/// throws on malformed data and stops after <see cref="MaxDecodedValues"/> values or <see cref="MaxDecodedBytes"/> bytes,
/// because dynamic offsets can alias the same region to expand a small payload into a huge result.</para>
/// </remarks>
public static class McpAbiCodec
{
    /// <summary>The maximum number of values produced by one decode.</summary>
    public const int MaxDecodedValues = 16_384;

    /// <summary>The maximum number of <c>bytes</c>/<c>string</c> bytes produced by one decode.</summary>
    public const int MaxDecodedBytes = 1024 * 1024;

    private const int WordSize = 32;
    private const int MaxIntegerTextLength = 80;
    private static readonly BigInteger TwoTo256 = BigInteger.One << 256;

    /// <summary>Encodes a function call: selector followed by the ABI-encoded <paramref name="args"/>.</summary>
    /// <param name="function">The function signature.</param>
    /// <param name="args">One JSON value per input, in order.</param>
    /// <param name="maxBytes">The maximum size of the resulting call data.</param>
    /// <param name="data">The call data.</param>
    /// <param name="error">A client-safe description naming the offending argument and the expected type.</param>
    public static bool TryEncodeCall(McpAbiSignature function, IReadOnlyList<JsonElement>? args, int maxBytes, [NotNullWhen(true)] out byte[]? data, [NotNullWhen(false)] out string? error)
    {
        data = null;
        if (!TryEncode(function.Inputs, args, maxBytes - function.Selector.Length, out byte[]? encoded, out error))
        {
            return false;
        }

        data = new byte[function.Selector.Length + encoded.Length];
        function.Selector.CopyTo(data, 0);
        encoded.CopyTo(data, function.Selector.Length);
        return true;
    }

    /// <summary>ABI-encodes <paramref name="args"/> as a tuple of <paramref name="parameters"/>.</summary>
    public static bool TryEncode(IReadOnlyList<McpAbiParam> parameters, IReadOnlyList<JsonElement>? args, int maxBytes, [NotNullWhen(true)] out byte[]? data, [NotNullWhen(false)] out string? error)
    {
        data = null;
        int count = args?.Count ?? 0;
        if (count != parameters.Count)
        {
            error = $"expected {parameters.Count} argument(s) {McpAbiSignature.FormatParams(parameters)} but got {count}";
            return false;
        }

        try
        {
            Encoder encoder = new(Math.Max(0, maxBytes));
            McpAbiType[] types = new McpAbiType[parameters.Count];
            for (int i = 0; i < types.Length; i++) types[i] = parameters[i].Type;
            data = encoder.EncodeSequence(types, args ?? [], "args", Names(parameters));
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Decodes <paramref name="data"/> as a tuple of <paramref name="parameters"/>.</summary>
    /// <param name="parameters">The expected types.</param>
    /// <param name="data">The encoded data.</param>
    /// <param name="values">One JSON-friendly value per parameter.</param>
    /// <param name="error">Why the data does not match the types.</param>
    public static bool TryDecode(IReadOnlyList<McpAbiParam> parameters, ReadOnlySpan<byte> data, [NotNullWhen(true)] out object?[]? values, [NotNullWhen(false)] out string? error)
    {
        McpAbiType[] types = new McpAbiType[parameters.Count];
        for (int i = 0; i < types.Length; i++) types[i] = parameters[i].Type;
        return TryDecode(types, data, out values, out error);
    }

    /// <summary>Decodes <paramref name="data"/> as a tuple of <paramref name="types"/>.</summary>
    public static bool TryDecode(IReadOnlyList<McpAbiType> types, ReadOnlySpan<byte> data, [NotNullWhen(true)] out object?[]? values, [NotNullWhen(false)] out string? error)
    {
        values = null;
        if (types.Count == 0)
        {
            values = [];
            error = null;
            return true;
        }

        if (data.Length == 0)
        {
            error = "the data is empty";
            return false;
        }

        Decoder decoder = new(data.ToArray());
        if (!decoder.TryDecodeSequence(types, 0, data.Length, out values))
        {
            error = decoder.Error ?? "the data does not match the types";
            values = null;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Decodes a single static value from a 32-byte word (such as an indexed event topic).</summary>
    /// <returns><see langword="false"/> if the type is dynamic or the word is not a canonical encoding of it.</returns>
    public static bool TryDecodeWord(McpAbiType type, ReadOnlySpan<byte> word, out object? value)
    {
        value = null;
        if (word.Length != WordSize || type.IsDynamic || type.HeadSize != WordSize || type.Kind is McpAbiTypeKind.Tuple or McpAbiTypeKind.FixedArray)
        {
            return false;
        }

        Decoder decoder = new(word.ToArray());
        return decoder.TryDecodeElementary(type, 0, out value);
    }

    /// <summary>Decodes <paramref name="log"/> against the event <paramref name="signature"/>.</summary>
    /// <param name="signature">An event signature.</param>
    /// <param name="log">The log.</param>
    /// <param name="standard">The standard to report in the result.</param>
    /// <param name="decoded">The decoded log. Indexed parameters of dynamic types (strings, bytes, arrays, tuples) are stored as their
    /// Keccak hash in the topic, so their value is that 32-byte hash as hex.</param>
    public static bool TryDecodeEvent(McpAbiSignature signature, LogEntry log, string? standard, [NotNullWhen(true)] out McpDecodedLog? decoded)
    {
        decoded = null;
        Hash256[] topics = log.Topics ?? [];
        int first = signature.Anonymous ? 0 : 1;
        if (topics.Length != signature.IndexedCount + first)
        {
            return false;
        }

        if (!signature.Anonymous && topics[0] != signature.Hash)
        {
            return false;
        }

        List<McpAbiParam> dataParams = new(signature.Inputs.Count);
        foreach (McpAbiParam input in signature.Inputs)
        {
            if (!input.Indexed) dataParams.Add(input);
        }

        object?[]? dataValues = [];
        if (dataParams.Count > 0 && !TryDecode(dataParams, log.Data ?? [], out dataValues, out _))
        {
            return false;
        }

        McpDecodedParam[] parameters = new McpDecodedParam[signature.Inputs.Count];
        int topicIndex = first;
        int dataIndex = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            McpAbiParam input = signature.Inputs[i];
            object? value;
            if (!input.Indexed)
            {
                value = dataValues[dataIndex++];
            }
            else
            {
                Hash256 topic = topics[topicIndex++];
                if (IsHashedWhenIndexed(input.Type))
                {
                    value = topic.ToString();
                }
                else if (!TryDecodeWord(input.Type, topic.Bytes, out value))
                {
                    return false;
                }
            }

            parameters[i] = new McpDecodedParam(input.Name, input.Type.CanonicalName, input.Indexed, value);
        }

        decoded = new McpDecodedLog(signature.Name, signature.CanonicalSignature, standard, parameters);
        return true;
    }

    /// <summary>Formats a value decoded by this codec as a short single-line text, for messages.</summary>
    public static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        List<object?> list => "[" + string.Join(", ", list.Select(FormatValue)) + "]",
        _ => value.ToString() ?? string.Empty
    };

    private static bool IsHashedWhenIndexed(McpAbiType type) =>
        type.Kind is McpAbiTypeKind.Bytes or McpAbiTypeKind.String or McpAbiTypeKind.Array or McpAbiTypeKind.FixedArray or McpAbiTypeKind.Tuple;

    private static string[] Names(IReadOnlyList<McpAbiParam> parameters)
    {
        string[] names = new string[parameters.Count];
        for (int i = 0; i < names.Length; i++) names[i] = parameters[i].Name;
        return names;
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Null => "null",
        _ => "nothing"
    };

    private sealed class Encoder(int maxBytes)
    {
        private long _budget = maxBytes;

        // Every produced word is charged exactly once (offsets here, values in EncodeValue), so the budget equals the output size.
        public byte[] EncodeSequence(IReadOnlyList<McpAbiType> types, IReadOnlyList<JsonElement> values, string path, IReadOnlyList<string>? names)
        {
            int headSize = 0;
            foreach (McpAbiType type in types) headSize += type.HeadSize;

            byte[][] heads = new byte[types.Count][];
            List<byte[]> tails = [];
            int tailOffset = headSize;
            for (int i = 0; i < types.Count; i++)
            {
                string elementPath = names is not null && names[i].Length > 0 ? $"{path}[{i}] ({names[i]})" : $"{path}[{i}]";
                McpAbiType type = types[i];
                if (type.IsDynamic)
                {
                    Consume(WordSize, elementPath);
                    byte[] tail = EncodeValue(type, values[i], elementPath);
                    heads[i] = Word(new BigInteger(tailOffset));
                    tails.Add(tail);
                    tailOffset += tail.Length;
                }
                else
                {
                    heads[i] = EncodeValue(type, values[i], elementPath);
                }
            }

            byte[] result = new byte[tailOffset];
            int position = 0;
            foreach (byte[] head in heads)
            {
                head.CopyTo(result, position);
                position += head.Length;
            }

            foreach (byte[] tail in tails)
            {
                tail.CopyTo(result, position);
                position += tail.Length;
            }

            return result;
        }

        private byte[] EncodeValue(McpAbiType type, JsonElement value, string path)
        {
            switch (type.Kind)
            {
                case McpAbiTypeKind.Address:
                    Consume(WordSize, path);
                    return EncodeAddress(value, path);
                case McpAbiTypeKind.Bool:
                    Consume(WordSize, path);
                    return EncodeBool(value, path);
                case McpAbiTypeKind.UInt:
                case McpAbiTypeKind.Int:
                    Consume(WordSize, path);
                    return EncodeInteger(type, value, path);
                case McpAbiTypeKind.FixedBytes:
                    {
                        Consume(WordSize, path);
                        byte[] bytes = ParseHex(value, path, type);
                        if (bytes.Length != type.Size)
                        {
                            throw new FormatException($"{path}: expected {type.CanonicalName} (exactly {type.Size} bytes as 0x hex) but got {bytes.Length} bytes");
                        }

                        byte[] word = new byte[WordSize];
                        bytes.CopyTo(word, 0);
                        return word;
                    }
                case McpAbiTypeKind.Bytes:
                    return EncodeDynamicBytes(ParseHex(value, path, type), path);
                case McpAbiTypeKind.String:
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException($"{path}: expected string (a JSON string) but got {Describe(value)}");
                    }

                    return EncodeDynamicBytes(Encoding.UTF8.GetBytes(value.GetString()!), path);
                case McpAbiTypeKind.Array:
                case McpAbiTypeKind.FixedArray:
                    {
                        if (value.ValueKind != JsonValueKind.Array)
                        {
                            throw new FormatException($"{path}: expected {type.CanonicalName} (a JSON array) but got {Describe(value)}");
                        }

                        int length = value.GetArrayLength();
                        if (type.Kind == McpAbiTypeKind.FixedArray && length != type.Size)
                        {
                            throw new FormatException($"{path}: expected {type.CanonicalName} with exactly {type.Size} elements but got {length}");
                        }

                        JsonElement[] elements = [.. value.EnumerateArray()];
                        McpAbiType[] types = new McpAbiType[length];
                        Array.Fill(types, type.Element!);
                        if (type.Kind == McpAbiTypeKind.FixedArray)
                        {
                            return EncodeSequence(types, elements, path, null);
                        }

                        Consume(WordSize, path);
                        byte[] body = EncodeSequence(types, elements, path, null);
                        byte[] result = new byte[WordSize + body.Length];
                        Word(new BigInteger(length)).CopyTo(result, 0);
                        body.CopyTo(result, WordSize);
                        return result;
                    }
                default:
                    {
                        JsonElement[] elements = TupleValues(type, value, path);
                        McpAbiType[] types = new McpAbiType[type.Components.Count];
                        string[] names = new string[types.Length];
                        for (int i = 0; i < types.Length; i++)
                        {
                            types[i] = type.Components[i].Type;
                            names[i] = type.Components[i].Name;
                        }

                        return EncodeSequence(types, elements, path, names);
                    }
            }
        }

        private static JsonElement[] TupleValues(McpAbiType type, JsonElement value, string path)
        {
            IReadOnlyList<McpAbiParam> components = type.Components;
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() != components.Count)
                {
                    throw new FormatException($"{path}: expected tuple {McpAbiSignature.FormatParams(components)} with {components.Count} elements but got {value.GetArrayLength()}");
                }

                return [.. value.EnumerateArray()];
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                JsonElement[] result = new JsonElement[components.Count];
                for (int i = 0; i < components.Count; i++)
                {
                    string name = components[i].Name;
                    if (name.Length == 0 || !value.TryGetProperty(name, out result[i]))
                    {
                        throw new FormatException(name.Length == 0
                            ? $"{path}: tuple {McpAbiSignature.FormatParams(components)} has unnamed components; pass it as a JSON array"
                            : $"{path}: missing tuple member '{name}' of {McpAbiSignature.FormatParams(components)}");
                    }
                }

                return result;
            }

            throw new FormatException($"{path}: expected tuple {McpAbiSignature.FormatParams(components)} (a JSON array, or an object keyed by component name) but got {Describe(value)}");
        }

        private byte[] EncodeDynamicBytes(byte[] bytes, string path)
        {
            int padded = (bytes.Length + WordSize - 1) / WordSize * WordSize;
            Consume(WordSize + (long)padded, path);
            byte[] result = new byte[WordSize + padded];
            Word(new BigInteger(bytes.Length)).CopyTo(result, 0);
            bytes.CopyTo(result, WordSize);
            return result;
        }

        private static byte[] EncodeAddress(JsonElement value, string path)
        {
            string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text is null || text.Length != 42 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !IsHex(text.AsSpan(2)))
            {
                throw new FormatException($"{path}: expected address (0x followed by 40 hex characters) but got {Describe(value)}");
            }

            Address address = new(Convert.FromHexString(text.AsSpan(2)));
            ReadOnlySpan<char> digits = text.AsSpan(2);
            bool mixedCase = digits.ContainsAnyInRange('a', 'f') && digits.ContainsAnyInRange('A', 'F');
            if (mixedCase && !address.ToString(true, true).AsSpan(2).SequenceEqual(digits))
            {
                throw new FormatException($"{path}: address has an invalid EIP-55 checksum; pass it all-lowercase or with the correct checksum");
            }

            byte[] word = new byte[WordSize];
            address.Bytes.CopyTo(word.AsSpan(WordSize - Address.Size));
            return word;
        }

        private static byte[] EncodeBool(JsonElement value, string path)
        {
            bool flag = value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when value.GetString() is "true" => true,
                JsonValueKind.String when value.GetString() is "false" => false,
                _ => throw new FormatException($"{path}: expected bool (true or false) but got {Describe(value)}")
            };

            byte[] word = new byte[WordSize];
            word[^1] = flag ? (byte)1 : (byte)0;
            return word;
        }

        private static byte[] EncodeInteger(McpAbiType type, JsonElement value, string path)
        {
            if (!TryParseInteger(value, out BigInteger number))
            {
                throw new FormatException($"{path}: expected {type.CanonicalName} as a decimal string (\"1000\"), a 0x-hex string or an integer JSON number but got {Describe(value)}");
            }

            BigInteger min = type.Kind == McpAbiTypeKind.UInt ? BigInteger.Zero : -(BigInteger.One << (type.Size - 1));
            BigInteger max = type.Kind == McpAbiTypeKind.UInt ? (BigInteger.One << type.Size) - 1 : (BigInteger.One << (type.Size - 1)) - 1;
            if (number < min || number > max)
            {
                throw new FormatException($"{path}: {number} is out of range for {type.CanonicalName} ({min} to {max})");
            }

            return Word(number.Sign < 0 ? number + TwoTo256 : number);
        }

        private static byte[] ParseHex(JsonElement value, string path, McpAbiType type)
        {
            string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length % 2 != 0 || !IsHex(text.AsSpan(2)))
            {
                throw new FormatException($"{path}: expected {type.CanonicalName} as 0x-prefixed hex with an even number of digits but got {Describe(value)}");
            }

            return Convert.FromHexString(text.AsSpan(2));
        }

        private void Consume(long bytes, string path)
        {
            _budget -= bytes;
            if (_budget < 0)
            {
                throw new FormatException($"{path}: the encoded call data exceeds the {maxBytes}-byte limit");
            }
        }
    }

    /// <summary>Parses an integer given as a decimal string (optionally negative), a 0x-hex string or an integer JSON number.</summary>
    internal static bool TryParseInteger(JsonElement value, out BigInteger number)
    {
        number = default;
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        if (text is null || text.Length is 0 or > MaxIntegerTextLength)
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        bool negative = span.StartsWith("-");
        if (negative) span = span[1..];
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            if (span.Length == 0 || !IsHex(span)) return false;
            number = BigInteger.Parse(string.Concat("0", span), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        else
        {
            if (span.Length == 0) return false;
            foreach (char c in span)
            {
                if (!char.IsAsciiDigit(c)) return false;
            }

            number = BigInteger.Parse(span, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (negative) number = -number;
        return true;
    }

    private static byte[] Word(BigInteger value)
    {
        byte[] word = new byte[WordSize];
        value.TryWriteBytes(word, out int written, isUnsigned: true, isBigEndian: true);
        if (written < WordSize)
        {
            Array.Copy(word, 0, word, WordSize - written, written);
            Array.Clear(word, 0, WordSize - written);
        }

        return word;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }

    private sealed class Decoder(byte[] data)
    {
        private int _values;
        private long _bytes;

        public string? Error { get; private set; }

        // Decodes a sequence whose head starts at `start`; offsets are relative to `start` and must stay below `end`.
        public bool TryDecodeSequence(IReadOnlyList<McpAbiType> types, int start, int end, [NotNullWhen(true)] out object?[]? values)
        {
            values = new object?[types.Count];
            int head = start;
            for (int i = 0; i < types.Count; i++)
            {
                McpAbiType type = types[i];
                if (head + (long)type.HeadSize > end)
                {
                    return Fail($"the data is too short for {type.CanonicalName} at byte {head}");
                }

                int position = head;
                if (type.IsDynamic)
                {
                    if (!TryReadLength(head, out int offset) || start + (long)offset >= end)
                    {
                        return Fail($"invalid offset for {type.CanonicalName} at byte {head}");
                    }

                    position = start + offset;
                }

                if (!TryDecodeValue(type, position, end, out values[i]))
                {
                    return false;
                }

                head += type.HeadSize;
            }

            return true;
        }

        public bool TryDecodeElementary(McpAbiType type, int position, out object? value)
        {
            value = null;
            if (position + (long)WordSize > data.Length)
            {
                return Fail($"the data is too short for {type.CanonicalName}");
            }

            ReadOnlySpan<byte> word = data.AsSpan(position, WordSize);
            switch (type.Kind)
            {
                case McpAbiTypeKind.Address:
                    if (word[..12].ContainsAnyExcept((byte)0)) return Fail("an address has non-zero padding");
                    value = new Address(word[12..]).ToString(true, true);
                    return true;
                case McpAbiTypeKind.Bool:
                    if (word[..31].ContainsAnyExcept((byte)0) || word[31] > 1) return Fail("a bool is not 0 or 1");
                    value = word[31] == 1;
                    return true;
                case McpAbiTypeKind.UInt:
                    {
                        int unusedBytes = (256 - type.Size) / 8;
                        if (word[..unusedBytes].ContainsAnyExcept((byte)0)) return Fail($"a value does not fit {type.CanonicalName}");
                        value = new BigInteger(word, isUnsigned: true, isBigEndian: true).ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                case McpAbiTypeKind.Int:
                    {
                        BigInteger number = new(word, isUnsigned: false, isBigEndian: true);
                        BigInteger limit = BigInteger.One << (type.Size - 1);
                        if (number < -limit || number >= limit) return Fail($"a value does not fit {type.CanonicalName}");
                        value = number.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                case McpAbiTypeKind.FixedBytes:
                    if (word[type.Size..].ContainsAnyExcept((byte)0)) return Fail($"a {type.CanonicalName} value has non-zero padding");
                    value = "0x" + Convert.ToHexStringLower(word[..type.Size]);
                    return true;
                default:
                    return Fail($"{type.CanonicalName} is not an elementary type");
            }
        }

        private bool TryDecodeValue(McpAbiType type, int position, int end, out object? value)
        {
            value = null;
            if (++_values > MaxDecodedValues)
            {
                return Fail($"the data decodes to more than {MaxDecodedValues} values");
            }

            switch (type.Kind)
            {
                case McpAbiTypeKind.Bytes:
                case McpAbiTypeKind.String:
                    {
                        if (position + (long)WordSize > end || !TryReadLength(position, out int length) || position + WordSize + (long)length > end)
                        {
                            return Fail($"invalid length for {type.CanonicalName} at byte {position}");
                        }

                        _bytes += length;
                        if (_bytes > MaxDecodedBytes)
                        {
                            return Fail($"the data decodes to more than {MaxDecodedBytes} bytes");
                        }

                        ReadOnlySpan<byte> bytes = data.AsSpan(position + WordSize, length);
                        // Decoded strings are untrusted contract output shown to an LLM, so they are sanitized and capped here, once for every tool.
                        value = type.Kind == McpAbiTypeKind.String
                            ? McpText.Sanitize(Encoding.UTF8.GetString(bytes), McpText.MaxDecodedStringLength, controlsAsSpace: true, truncationMarker: McpText.TruncationMarker)
                            : "0x" + Convert.ToHexStringLower(bytes);
                        return true;
                    }
                case McpAbiTypeKind.Array:
                    {
                        if (position + (long)WordSize > end || !TryReadLength(position, out int length))
                        {
                            return Fail($"invalid length for {type.CanonicalName} at byte {position}");
                        }

                        int bodyStart = position + WordSize;
                        if ((long)length * type.Element!.HeadSize > end - bodyStart)
                        {
                            return Fail($"{type.CanonicalName} claims {length} elements, more than the data holds");
                        }

                        return TryDecodeList(type.Element, length, bodyStart, end, out value);
                    }
                case McpAbiTypeKind.FixedArray:
                    return TryDecodeList(type.Element!, type.Size, position, end, out value);
                case McpAbiTypeKind.Tuple:
                    {
                        McpAbiType[] types = new McpAbiType[type.Components.Count];
                        for (int i = 0; i < types.Length; i++) types[i] = type.Components[i].Type;
                        if (!TryDecodeSequence(types, position, end, out object?[]? components)) return false;
                        value = new List<object?>(components);
                        return true;
                    }
                default:
                    return TryDecodeElementary(type, position, out value);
            }
        }

        private bool TryDecodeList(McpAbiType element, int length, int start, int end, out object? value)
        {
            value = null;
            McpAbiType[] types = new McpAbiType[length];
            Array.Fill(types, element);
            if (!TryDecodeSequence(types, start, end, out object?[]? items)) return false;
            value = new List<object?>(items);
            return true;
        }

        private bool TryReadLength(int position, out int value)
        {
            value = 0;
            ReadOnlySpan<byte> word = data.AsSpan(position, WordSize);
            if (word[..28].ContainsAnyExcept((byte)0)) return false;
            uint number = BinaryPrimitives.ReadUInt32BigEndian(word[28..]);
            if (number > int.MaxValue) return false;
            value = (int)number;
            return true;
        }

        private bool Fail(string error)
        {
            Error ??= error;
            return false;
        }
    }
}
