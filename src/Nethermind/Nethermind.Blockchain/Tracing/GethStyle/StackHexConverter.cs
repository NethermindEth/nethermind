// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Converts a stack snapshot to and from Geth-style hex words.
/// </summary>
public sealed class StackHexConverter : JsonConverter<ReadOnlyMemory<byte>>
{
    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        ReadOnlySpan<byte> span = value.Span;
        for (int offset = 0; offset < span.Length; offset += EvmStack.WordSize)
            HexWriter.WriteUInt256HexRawValue(writer,
                new UInt256(span.Slice(offset, EvmStack.WordSize), isBigEndian: true),
                zeroPadded: false, addHexPrefix: true);
        writer.WriteEndArray();
    }

    public override ReadOnlyMemory<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using ArrayPoolList<byte> raw = new(64);
        Span<byte> wordBuf = stackalloc byte[EvmStack.WordSize];
        while (reader.Read() && reader.TokenType == JsonTokenType.String)
        {
            ReadOnlySpan<byte> decoded = Bytes.FromHexString(reader.GetString()!);
            wordBuf.Clear();
            decoded.CopyTo(wordBuf.Slice(EvmStack.WordSize - decoded.Length));
            raw.AddRange(wordBuf);
        }
        return raw.AsSpan().ToArray();
    }
}
