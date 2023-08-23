// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Proofs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Serialization.Json;


public class IpaProofConverter: JsonConverter<IpaProofStruct>
{
    public override void WriteJson(JsonWriter writer, IpaProofStruct value, JsonSerializer serializer)
    {
        if (value is null)
        {
            value = new IpaProofStruct(Array.Empty<Banderwagon>(), FrE.Zero, Array.Empty<Banderwagon>());
        }

        writer.WriteStartObject();

        writer.WritePropertyName("cl");
        writer.WriteStartArray();
        for (int i = 0; i < value.L.Length; i++)
        {
            writer.WriteValue(value.L[i].ToBytes().ToHexString(true));
        }
        writer.WriteEnd();

        writer.WritePropertyName("cr");
        writer.WriteStartArray();
        for (int i = 0; i < value.R.Length; i++)
        {
            writer.WriteValue(value.R[i].ToBytes().ToHexString(true));
        }
        writer.WriteEnd();

        writer.WritePropertyName("finalEvaluation");
        serializer.Serialize(writer, value.A.ToBytesBigEndian());

        writer.WriteEndObject();
    }

    public override IpaProofStruct ReadJson(JsonReader reader, Type objectType, IpaProofStruct existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        Banderwagon[] cl = Array.Empty<Banderwagon>();
        Banderwagon[] cr = Array.Empty<Banderwagon>();
        if (reader.TokenType == JsonToken.StartObject)
        {
            for (int i = 0; i < 2; i++)
            {
                reader.Read();
                if (string.Equals((string)reader.Value, "cl", StringComparison.InvariantCultureIgnoreCase))
                {
                    reader.Read();
                    cl = serializer.Deserialize<Banderwagon[]>(reader) ??
                         throw new InvalidOperationException();
                }
                else if (string.Equals((string)reader.Value, "cr", StringComparison.InvariantCultureIgnoreCase))
                {
                    reader.Read();
                    cr = serializer.Deserialize<Banderwagon[]>(reader) ??
                         throw new InvalidOperationException();
                }
            }
        }
        reader.Read();
        FrE finalEvaluation = FrE.FromBytes(Bytes.FromHexString(reader.ReadAsString()), true);
        reader.Read();
        return new IpaProofStruct(cl, finalEvaluation, cr);
    }
}
