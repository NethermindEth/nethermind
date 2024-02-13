// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Proofs;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Nethermind.Serialization.Json;

public class IpaProofConverter : System.Text.Json.Serialization.JsonConverter<IpaProofStruct>
{
    public override IpaProofStruct Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Banderwagon[] cl = Array.Empty<Banderwagon>();
        Banderwagon[] cr = Array.Empty<Banderwagon>();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            for (int i = 0; i < 2; i++)
            {
                reader.Read();
                if (reader.ValueTextEquals("cl"u8))
                {
                    reader.Read();
                    cl = JsonSerializer.Deserialize<Banderwagon[]>(ref reader, options) ??
                         throw new InvalidOperationException();
                }
                else if (reader.ValueTextEquals("cr"u8))
                {
                    reader.Read();
                    cr = JsonSerializer.Deserialize<Banderwagon[]>(ref reader, options) ??
                         throw new InvalidOperationException();
                }
            }
        }
        reader.Read();
        ReadOnlySpan<byte> hex = JsonSerializer.Deserialize<byte[]>(ref reader, options);
        FrE finalEvaluation = FrE.FromBytes(hex, true);
        reader.Read();
        return new IpaProofStruct(cl, finalEvaluation, cr);
    }

    public override void Write(Utf8JsonWriter writer, IpaProofStruct value, JsonSerializerOptions options)
    {
        // if (value is null)
        // {
        //     value = new IpaProofStruct(Array.Empty<Banderwagon>(), FrE.Zero, Array.Empty<Banderwagon>());
        // }

        writer.WriteStartObject();

        writer.WritePropertyName("cl"u8);
        writer.WriteStartArray();
        for (int i = 0; i < value.L.Length; i++)
        {
            ByteArrayConverter.Convert(writer, value.L[i].ToBytes(), skipLeadingZeros: false);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("cr"u8);
        writer.WriteStartArray();
        for (int i = 0; i < value.R.Length; i++)
        {
            ByteArrayConverter.Convert(writer, value.R[i].ToBytes(), skipLeadingZeros: false);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("finalEvaluation");
        ByteArrayConverter.Convert(writer, value.A.ToBytesBigEndian(), skipLeadingZeros: false);

        writer.WriteEndObject();
    }
}

