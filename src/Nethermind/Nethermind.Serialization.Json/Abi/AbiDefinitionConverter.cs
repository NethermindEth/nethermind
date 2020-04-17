//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Abi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Serialization.Json.Abi
{
    public class AbiDefinitionConverter : JsonConverter<AbiDefinition>
    {
        public override void WriteJson(JsonWriter writer, AbiDefinition value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            
            foreach (var item in value.Items)
            {
                serializer.Serialize(writer, item);
            }
            
            writer.WriteEndArray();
        }
        
        public override AbiDefinition ReadJson(JsonReader reader, Type objectType, AbiDefinition existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            existingValue ??= new AbiDefinition();
            foreach (var definitionToken in token.Children())
            {
                var type = Enum.Parse<AbiDescriptionType>(definitionToken[nameof(AbiBaseDescription<AbiParameter>.Type).ToLowerInvariant()].Value<string>(), true);
                var name = definitionToken[nameof(AbiBaseDescription<AbiParameter>.Name).ToLowerInvariant()]?.Value<string>();
                if (type == AbiDescriptionType.Event)
                {
                    var abiEvent = new AbiEventDescription();
                    serializer.Populate(definitionToken.CreateReader(), abiEvent);
                    existingValue.Add(abiEvent);
                }
                else
                {
                    var abiFunction = new AbiFunctionDescription();
                    serializer.Populate(definitionToken.CreateReader(), abiFunction);
                    existingValue.Add(abiFunction);
                }
            }

            return existingValue;
        }
    }
}