// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Canonical JSON for the native profile's original chain document.</summary>
/// <remarks>Preserves number tokens and emits non-control Unicode directly as UTF-8.</remarks>
internal static class RollupXCanonicalJson
{
    private const int MaxDepth = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> input)
    {
        ValidateStrings(input.Span);
        using JsonDocument document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = MaxDepth });
        ArrayBufferWriter<byte> output = new(input.Length);
        Write(document.RootElement, output);
        return output.WrittenMemory;
    }

    private static void ValidateStrings(ReadOnlySpan<byte> input)
    {
        try { StrictUtf8.GetCharCount(input); }
        catch (DecoderFallbackException exception) { throw new JsonException("Invalid UTF-8 in chain document.", exception); }
        Utf8JsonReader reader = new(input, new JsonReaderOptions { MaxDepth = MaxDepth });
        while (reader.Read())
            if ((reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) && reader.ValueIsEscaped)
                ValidateSurrogates(reader.ValueSpan);
    }

    private static void ValidateSurrogates(ReadOnlySpan<byte> text)
    {
        for (int index = 0; index < text.Length;)
        {
            int escape = text[index..].IndexOf((byte)'\\');
            if (escape < 0) return;
            index += escape + 1;
            if (text[index++] != (byte)'u') continue;
            char high = HexQuad(text.Slice(index, 4));
            index += 4;
            if (char.IsLowSurrogate(high)) throw new JsonException("Unpaired JSON surrogate.");
            if (!char.IsHighSurrogate(high)) continue;
            if (text.Length - index < 6 || text[index] != (byte)'\\' || text[index + 1] != (byte)'u' ||
                !char.IsLowSurrogate(HexQuad(text.Slice(index + 2, 4))))
                throw new JsonException("Unpaired JSON surrogate.");
            index += 6;
        }
    }

    private static char HexQuad(ReadOnlySpan<byte> text)
    {
        int value = 0;
        foreach (byte digit in text)
        {
            int decoded = digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - '0',
                >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
                _ => throw new JsonException("Invalid JSON Unicode escape."),
            };
            value = (value << 4) | decoded;
        }
        return (char)value;
    }

    private static void Write(JsonElement element, ArrayBufferWriter<byte> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: WriteObject(element, output); break;
            case JsonValueKind.Array: WriteArray(element, output); break;
            case JsonValueKind.String: WriteString(element.GetString()!, output); break;
            case JsonValueKind.Number: output.Write(JsonMarshal.GetRawUtf8Value(element)); break;
            case JsonValueKind.True: output.Write("true"u8); break;
            case JsonValueKind.False: output.Write("false"u8); break;
            case JsonValueKind.Null: output.Write("null"u8); break;
            default: throw new JsonException("Unsupported JSON value.");
        }
    }

    private static void WriteObject(JsonElement element, ArrayBufferWriter<byte> output)
    {
        List<Property> properties = new(element.GetPropertyCount());
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = property.Name;
            properties.Add(new Property(StrictUtf8.GetBytes(name), property.Value));
        }
        properties.Sort(static (left, right) => left.Utf8Name.AsSpan().SequenceCompareTo(right.Utf8Name));
        for (int index = 1; index < properties.Count; index++)
            if (properties[index].Utf8Name.AsSpan().SequenceEqual(properties[index - 1].Utf8Name))
                throw new JsonException("Duplicate JSON property.");
        WriteByte((byte)'{', output);
        for (int index = 0; index < properties.Count; index++)
        {
            if (index != 0) WriteByte((byte)',', output);
            Property property = properties[index];
            WriteString(property.Utf8Name, output);
            WriteByte((byte)':', output);
            Write(property.Value, output);
        }
        WriteByte((byte)'}', output);
    }

    private static void WriteArray(JsonElement element, ArrayBufferWriter<byte> output)
    {
        WriteByte((byte)'[', output);
        bool first = true;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (!first) WriteByte((byte)',', output);
            first = false;
            Write(item, output);
        }
        WriteByte((byte)']', output);
    }

    private static void WriteString(string value, ArrayBufferWriter<byte> output)
    {
        WriteByte((byte)'"', output);
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character >= ' ' && character is not ('"' or '\\')) continue;
            WriteUtf8(value.AsSpan(start, index - start), output);
            WriteEscape(character, output);
            start = index + 1;
        }
        WriteUtf8(value.AsSpan(start), output);
        WriteByte((byte)'"', output);
    }

    private static void WriteString(ReadOnlySpan<byte> value, ArrayBufferWriter<byte> output)
    {
        WriteByte((byte)'"', output);
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            byte character = value[index];
            if (character >= ' ' && character is not ((byte)'"' or (byte)'\\')) continue;
            output.Write(value[start..index]);
            WriteEscape((char)character, output);
            start = index + 1;
        }
        output.Write(value[start..]);
        WriteByte((byte)'"', output);
    }

    private static void WriteEscape(char character, ArrayBufferWriter<byte> output)
    {
        byte shortEscape = character switch
        {
            '"' => (byte)'"', '\\' => (byte)'\\', '\b' => (byte)'b', '\f' => (byte)'f',
            '\n' => (byte)'n', '\r' => (byte)'r', '\t' => (byte)'t', _ => 0,
        };
        if (shortEscape != 0)
        {
            output.Write([(byte)'\\', shortEscape]);
            return;
        }
        ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
        Span<byte> bytes = output.GetSpan(6);
        "\\u00"u8.CopyTo(bytes);
        bytes[4] = hex[character >> 4];
        bytes[5] = hex[character & 15];
        output.Advance(6);
    }

    private static void WriteUtf8(ReadOnlySpan<char> text, ArrayBufferWriter<byte> output)
    {
        int length = StrictUtf8.GetByteCount(text);
        int written = StrictUtf8.GetBytes(text, output.GetSpan(length));
        output.Advance(written);
    }

    private static void WriteByte(byte value, ArrayBufferWriter<byte> output)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private readonly record struct Property(byte[] Utf8Name, JsonElement Value);
}
