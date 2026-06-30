// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Serializes a per-opcode memory snapshot stored as raw bytes into the Geth-style array of
/// <c>0x</c>-prefixed, zero-padded 32-byte hex words, and vice versa. Hex encoding is deferred
/// to write time — no per-word string allocations occur during tracing.
/// </summary>
public sealed class MemoryHexConverter : JsonConverter<ReadOnlyMemory<byte>>
{
    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        ReadOnlySpan<byte> span = value.Span;
        for (int offset = 0; offset < span.Length; offset += EvmPooledMemory.WordSize)
            HexWriter.WriteFixed32HexRawValue(writer, span.Slice(offset, EvmPooledMemory.WordSize), addHexPrefix: true);
        writer.WriteEndArray();
    }

    public override ReadOnlyMemory<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using ArrayPoolList<byte> raw = new(64);
        Span<byte> wordBuf = stackalloc byte[EvmPooledMemory.WordSize];
        while (reader.Read() && reader.TokenType == JsonTokenType.String)
        {
            ReadOnlySpan<byte> decoded = Bytes.FromHexString(reader.GetString()!);
            wordBuf.Clear();
            decoded.CopyTo(wordBuf.Slice(EvmPooledMemory.WordSize - decoded.Length));
            raw.AddRange(wordBuf);
        }
        return raw.AsSpan().ToArray();
    }
}
