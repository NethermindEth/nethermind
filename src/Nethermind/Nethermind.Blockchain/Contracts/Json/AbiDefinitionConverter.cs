// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FastEnumUtility;
using Nethermind.Abi;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Blockchain.Contracts.Json
{
    public class AbiDefinitionConverter : JsonConverter<AbiDefinition>
    {
        public override void WriteJson(JsonWriter writer, AbiDefinition value, JsonSerializer serializer)
        {
            writer.WriteStartArray();

            foreach (AbiBaseDescription item in value.Items)
            {
                serializer.Serialize(writer, item);
            }

            writer.WriteEndArray();
        }

        private readonly string _nameTokenName = nameof(AbiBaseDescription<AbiParameter>.Name).ToLowerInvariant();
        private readonly string _typeTokenName = nameof(AbiBaseDescription<AbiParameter>.Type).ToLowerInvariant();

        public override AbiDefinition ReadJson(
            JsonReader reader,
            Type objectType,
            AbiDefinition existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            JToken topLevelToken = JToken.Load(reader);
            existingValue ??= new AbiDefinition();

            JToken abiToken;
            if (topLevelToken.Type == JTokenType.Object)
            {
                abiToken = topLevelToken["abi"];
                byte[] bytecode = Bytes.FromHexString(topLevelToken["bytecode"]?.Value<string>() ?? string.Empty);
                byte[] deployedBytecode = Bytes.FromHexString(topLevelToken["deployedBytecode"]?.Value<string>() ?? string.Empty);
                existingValue.SetBytecode(bytecode);
                existingValue.SetDeployedBytecode(deployedBytecode);
            }
            else
            {
                abiToken = topLevelToken;
            }

            foreach (var definitionToken in abiToken?.Children() ?? Enumerable.Empty<JToken>())
            {
                string name = definitionToken[_nameTokenName]?.Value<string>();
                JToken typeToken = definitionToken[_typeTokenName];
                if (typeToken is null)
                {
                    continue;
                }

                AbiDescriptionType type = FastEnum.Parse<AbiDescriptionType>(typeToken.Value<string>(), true);

                if (type == AbiDescriptionType.Event)
                {
                    AbiEventDescription abiEvent = new();
                    serializer.Populate(definitionToken.CreateReader(), abiEvent);
                    existingValue.Add(abiEvent);
                }
                else if (type == AbiDescriptionType.Error)
                {
                    AbiErrorDescription abiError = new();
                    serializer.Populate(definitionToken.CreateReader(), abiError);
                    existingValue.Add(abiError);
                }
                else
                {
                    AbiFunctionDescription abiFunction = new();
                    serializer.Populate(definitionToken.CreateReader(), abiFunction);
                    existingValue.Add(abiFunction);
                }
            }

            return existingValue;
        }
    }
}
