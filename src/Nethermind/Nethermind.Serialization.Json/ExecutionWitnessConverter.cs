// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Verkle;

namespace Nethermind.Serialization.Json;

public class ExecutionWitnessConverter: System.Text.Json.Serialization.JsonConverter<ExecutionWitness>
{
    public override ExecutionWitness Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Read();
        reader.Read();
        StemStateDiff[] stateDiff = JsonSerializer.Deserialize<StemStateDiff[]>(ref reader, options);
        reader.Read();
        reader.Read();
        WitnessVerkleProof proof = JsonSerializer.Deserialize<WitnessVerkleProof>(ref reader, options);
        reader.Read();
        return new ExecutionWitness(stateDiff, proof);
    }

    public override void Write(Utf8JsonWriter writer, ExecutionWitness value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("stateDiff"u8);
        JsonSerializer.Serialize(writer, value.StateDiff, options);

        writer.WritePropertyName("verkleProof"u8);
        JsonSerializer.Serialize(writer, value.VerkleProof, options);

        writer.WriteEndObject();
    }
}
