// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Converts a transaction trace entry to the
/// <see href="https://jsonlines.org">JSON Lines</see> format.
/// This converter is write-only.
/// </summary>
internal class GethLikeTxTraceJsonLinesConverter : JsonConverter<GethTxFileTraceEntry>
{
    /// <summary>
    /// This method is not supported.
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public override GethTxFileTraceEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    /// <summary>
    /// Writes specified transaction entry as JSON adding a new line at the end.
    /// </summary>
    /// <remarks>
    /// This method flushes and resets the writer before returning.
    /// </remarks>
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, GethTxFileTraceEntry value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        writer.WritePropertyName("pc");
        writer.WriteNumberValue(value.ProgramCounter);

        writer.WritePropertyName("op");
        writer.WriteNumberValue((byte)value.OpcodeRaw);

        writer.WritePropertyName("gas");
        HexWriter.WriteUlongHexStringValue(writer, value.Gas);

        writer.WritePropertyName("gasCost");
        HexWriter.WriteUlongHexStringValue(writer, value.GasCost);

        if (value.Memory is { Length: > 0 } mem)
        {
            writer.WritePropertyName("memory");
            WriteMemoryBlob(writer, mem);
        }

        writer.WritePropertyName("memSize");
        writer.WriteNumberValue(value.MemorySize ?? 0UL);

        writer.WritePropertyName("stack");
        if (value.Stack is ReadOnlyMemory<byte> stack)
        {
            writer.WriteStartArray();

            ReadOnlySpan<byte> stackSpan = stack.Span;
            for (int offset = 0; offset < stackSpan.Length; offset += EvmStack.WordSize)
                HexWriter.WriteUInt256HexRawValue(writer,
                    new UInt256(stackSpan.Slice(offset, EvmStack.WordSize), isBigEndian: true),
                    zeroPadded: false, addHexPrefix: true);

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNullValue();
        }

        if (value.ReturnData is { } returnData)
        {
            writer.WritePropertyName("returnData");
            ByteArrayConverter.Convert(writer, returnData.Span, skipLeadingZeros: false);
        }

        writer.WritePropertyName("depth");
        writer.WriteNumberValue(value.Depth);

        writer.WritePropertyName("refund");
        writer.WriteNumberValue(value.Refund ?? 0L);

        writer.WritePropertyName("opName");
        writer.WriteStringValue(value.Opcode);

        if (value.Error is not null)
            WriteError(writer, value.Error);

        writer.WriteEndObject();

        WriteLineEnd(writer);
    }

    internal static void WriteLineEnd(Utf8JsonWriter writer)
    {
        // Reset between JSON values to avoid inserting commas into the line stream.
        writer.Flush();
        writer.Reset();
        writer.WriteRawValue("\n", true);
        writer.Flush();
        writer.Reset();
    }

    /// <summary>Writes an <c>error</c> property with the escaping of Geth's JSON encoder.</summary>
    /// <remarks>Go's <c>json.Encoder</c> escapes <c>&lt;</c> and <c>&gt;</c> with lowercase hex digits, as in Geth's
    /// <c>stack underflow (1 \u003c=\u003e 2)</c>.</remarks>
    internal static void WriteError(Utf8JsonWriter writer, string error)
    {
        string escaped = JsonEncodedText.Encode(error).ToString().Replace("\\u003C", "\\u003c").Replace("\\u003E", "\\u003e");
        writer.WritePropertyName("error");
        writer.WriteRawValue($"\"{escaped}\"", skipInputValidation: true);
    }

    // The file format renders memory as a single contiguous 0x-prefixed hex blob, not a per-word array.
    private static void WriteMemoryBlob(Utf8JsonWriter writer, ReadOnlyMemory<byte> memory) =>
        HexWriter.WriteHexStringValue(writer, memory.Span);
}
