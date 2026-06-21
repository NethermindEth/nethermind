// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
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
        WriteHexLong(writer, value.Gas);

        writer.WritePropertyName("gasCost");
        WriteHexLong(writer, value.GasCost);

        writer.WritePropertyName("memSize");
        writer.WriteNumberValue(value.MemorySize ?? 0UL);

        if ((value.Memory?.Length ?? 0) != 0)
        {
            writer.WritePropertyName("memory");
            WriteMemoryBlob(writer, value.Memory!);
        }

        if (value.Stack is not null)
        {
            writer.WritePropertyName("stack");
            writer.WriteStartArray();

            foreach (UInt256 word in value.Stack)
                HexWriter.WriteUInt256HexRawValue(writer, word, zeroPadded: false, addHexPrefix: true);

            writer.WriteEndArray();
        }

        writer.WritePropertyName("depth");
        writer.WriteNumberValue(value.Depth);

        writer.WritePropertyName("refund");
        writer.WriteNumberValue(value.Refund ?? 0L);

        writer.WritePropertyName("opName");
        writer.WriteStringValue(value.Opcode);

        if (value.Error is not null)
        {
            writer.WritePropertyName("error");
            writer.WriteStringValue(value.Error);
        }

        writer.WriteEndObject();

        // Before writing a new line, flush and reset the writer
        // to avoid adding comma (depth tracking)
        writer.Flush();
        writer.Reset();
        writer.WriteRawValue(Environment.NewLine, true);
        // After writing the new line, flush and reset the writer again
        // to avoid adding comma on writer reuse
        writer.Flush();
        writer.Reset();
    }

    private static void WriteHexLong(Utf8JsonWriter writer, long v)
    {
        Span<char> buf = stackalloc char[18]; // "0x" + up to 16 hex digits
        buf[0] = '0';
        buf[1] = 'x';
        v.TryFormat(buf[2..], out int written, "x");
        writer.WriteStringValue(buf[..(2 + written)]);
    }

    private const int WordSize = 32;

    // The file format renders memory as a single contiguous 0x-prefixed hex blob (not a per-word array),
    // so the words are encoded straight into a UTF-8 buffer and written as one JSON string.
    private static void WriteMemoryBlob(Utf8JsonWriter writer, UInt256[] words)
    {
        int rawLength = words.Length * WordSize;
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);
        byte[] hex = ArrayPool<byte>.Shared.Rent(2 + rawLength * 2);
        try
        {
            Span<byte> rawSpan = raw.AsSpan(0, rawLength);
            for (int i = 0; i < words.Length; i++)
                words[i].ToBigEndian(rawSpan.Slice(i * WordSize, WordSize));

            hex[0] = (byte)'0';
            hex[1] = (byte)'x';
            ((ReadOnlySpan<byte>)rawSpan).OutputBytesToByteHex(hex.AsSpan(2, rawLength * 2), extraNibble: false);

            writer.WriteStringValue(hex.AsSpan(0, 2 + rawLength * 2));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(hex);
        }
    }
}
