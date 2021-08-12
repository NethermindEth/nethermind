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
using System.Linq;
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
                existingValue.SetBytecode(bytecode);   
            }
            else
            {
                abiToken = topLevelToken;
            }

            foreach (var definitionToken in abiToken?.Children() ?? Enumerable.Empty<JToken>())
            {
                string name = definitionToken[_nameTokenName]?.Value<string>();
                JToken typeToken = definitionToken[_typeTokenName];
                if (typeToken == null)
                {
                    continue;
                }
                
                AbiDescriptionType type = Enum.Parse<AbiDescriptionType>(
                    typeToken.Value<string>(), true);
                
                if (type == AbiDescriptionType.Event)
                {
                    AbiEventDescription abiEvent = new();
                    serializer.Populate(definitionToken.CreateReader(), abiEvent);
                    existingValue.Add(abiEvent);
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
