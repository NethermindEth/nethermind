// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Serializes the per-opcode memory snapshot kept as one <see cref="UInt256"/> per 32-byte word into the
/// Geth-style array of <c>0x</c>-prefixed, zero-padded 32-byte hex words without allocating per-word hex
/// strings during tracing — words are formatted only here, at write time.
/// </summary>
public sealed class MemoryHexConverter : JsonConverter<UInt256[]>
{
    public override void Write(Utf8JsonWriter writer, UInt256[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (UInt256 word in value)
        {
            HexWriter.WriteUInt256HexRawValue(writer, word, zeroPadded: true, addHexPrefix: true);
        }
        writer.WriteEndArray();
    }

    public override UInt256[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        List<UInt256> words = [];
        while (reader.Read() && reader.TokenType == JsonTokenType.String)
        {
            words.Add(new UInt256(Bytes.FromHexString(reader.GetString()!), isBigEndian: true));
        }
        return [.. words];
    }
}
