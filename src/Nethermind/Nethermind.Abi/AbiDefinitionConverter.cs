// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using FastEnumUtility;

using Nethermind.Abi;
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain.Contracts.Json;

public class AbiDefinitionConverter : JsonConverter<AbiDefinition>
{
    public override void Write(Utf8JsonWriter writer, AbiDefinition value, JsonSerializerOptions op)
    {
        writer.WriteStartArray();
        foreach (AbiBaseDescription item in value.Items)
        {
            JsonSerializer.Serialize(writer, item, op);
        }

        writer.WriteEndArray();
    }

    public override AbiDefinition? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions op)
    {
        AbiDefinition value = new();
        if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
            return null;
        JsonElement abiToken;
        JsonElement topLevelToken = document.RootElement;
        if (topLevelToken.ValueKind == JsonValueKind.Object)
        {
            abiToken = topLevelToken.GetProperty("abi"u8);
            if (topLevelToken.TryGetProperty("bytecode"u8, out JsonElement bytecodeBase64))
            {
                value.SetBytecode(Bytes.FromHexString(bytecodeBase64.GetString()!));
            }

            if (topLevelToken.TryGetProperty("deployedBytecode"u8, out JsonElement deployedBytecodeBase64))
            {
                value.SetDeployedBytecode(Bytes.FromHexString(deployedBytecodeBase64.GetString()!));
            }
        }
        else
        {
            abiToken = topLevelToken;
        }

        foreach (JsonElement definitionToken in abiToken.EnumerateArray())
        {
            if (!definitionToken.TryGetProperty("type"u8, out JsonElement typeToken))
            {
                continue;
            }

            AbiDescriptionType type = FastEnum.Parse<AbiDescriptionType>(typeToken.GetString(), true);
            switch (type)
            {
                case AbiDescriptionType.Event:
                    AbiEventDescription? eventDescription = definitionToken.Deserialize<AbiEventDescription>(op);
                    if (eventDescription is not null)
                        value.Add(eventDescription);
                    break;
                case AbiDescriptionType.Error:
                    AbiErrorDescription? errorDescription = definitionToken.Deserialize<AbiErrorDescription>(op);
                    if (errorDescription is not null)
                        value.Add(errorDescription);
                    break;
                default:
                    AbiFunctionDescription? functionDescription = definitionToken.Deserialize<AbiFunctionDescription>(op);
                    if (functionDescription is not null)
                        value.Add(functionDescription);
                    break;
            }
        }

        return value;
    }
}
