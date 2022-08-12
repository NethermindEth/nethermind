//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastEnumUtility;
using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    public class AbiDefinitionConverter : JsonConverter<AbiDefinition>
    {
        public override void Write(Utf8JsonWriter writer, AbiDefinition value, JsonSerializerOptions op)
        {
            writer.WriteStartArray();
            foreach (AbiBaseDescription item in value.Items)
            {
                JsonSerializer.Serialize<object>(writer, item, op);
            }

            writer.WriteEndArray();
        }

        //private readonly string _nameTokenName = nameof(AbiBaseDescription<AbiParameter>.Name).ToLowerInvariant();
        private readonly string _typeTokenName = nameof(AbiBaseDescription<AbiParameter>.Type).ToLowerInvariant();

        public override AbiDefinition? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions op)
        {
            AbiDefinition value = new();
            if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
                return null;
            JsonElement topLevelToken, abiToken;
            topLevelToken = document.RootElement;
            if (topLevelToken.ValueKind == JsonValueKind.Object)
            {
                abiToken = topLevelToken.GetProperty("abi");
                if (topLevelToken.TryGetProperty("bytecode", out JsonElement bytecodeBase64))
                    value.SetBytecode(bytecodeBase64.GetBytesFromBase64());
                if (topLevelToken.TryGetProperty("deployedBytecode", out JsonElement deployedBytecodeBase64))
                    value.SetDeployedBytecode(deployedBytecodeBase64.GetBytesFromBase64());
            }
            else
            {
                abiToken = topLevelToken;
            }
            foreach (JsonElement definitionToken in abiToken.EnumerateArray())
            {
                //string name = "";
                //if (!definitionToken.TryGetProperty(_nameTokenName, out JsonElement nameToken))
                    //name = nameToken.GetString();
                if (!definitionToken.TryGetProperty(_typeTokenName, out JsonElement typeToken))
                continue;
                AbiDescriptionType type = FastEnum.Parse<AbiDescriptionType>(typeToken.GetString(), true);
                switch (type)
                {
                    case AbiDescriptionType.Event:
                        AbiEventDescription? eventDescription = definitionToken.Deserialize(typeof(AbiEventDescription), op) as AbiEventDescription;
                        if (eventDescription != null)
                            value.Add(eventDescription);
                        break;
                    case AbiDescriptionType.Error:
                        AbiErrorDescription? errorDescription = definitionToken.Deserialize<AbiErrorDescription>(op);
                        if (errorDescription != null)
                            value.Add(errorDescription);
                        break;
                    default:
                        AbiFunctionDescription? functionDescription = definitionToken.Deserialize(typeof(AbiFunctionDescription), op) as AbiFunctionDescription;
                        if (functionDescription != null)
                            value.Add(functionDescription);
                        break;
                }
            }
            return value;
        }
    }
}
