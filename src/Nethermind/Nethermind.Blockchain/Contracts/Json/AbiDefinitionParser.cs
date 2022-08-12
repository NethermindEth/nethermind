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
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Nethermind.Abi;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Contracts.Json
{
    public class AbiDefinitionParser : IAbiDefinitionParser
    {
        private readonly JsonSerializerOptions _op;
        private readonly IList<IAbiTypeFactory> _abiTypeFactories = new List<IAbiTypeFactory>(); 

        public AbiDefinitionParser()
        {
            _op = GetJsonSerializerSettings();
        }

        public AbiDefinition Parse(string json, string name = null)
        {
            AbiDefinition definition = JsonSerializer.Deserialize<AbiDefinition>(json, _op);
            definition.Name = name;
            return definition;
        }

        public AbiDefinition Parse(Type type)
        {
            using var reader = LoadResource(type);
            AbiDefinition definition = JsonSerializer.Deserialize<AbiDefinition>(reader.BaseStream, _op);
            return definition;
        }

        public void RegisterAbiTypeFactory(IAbiTypeFactory abiTypeFactory)
        {
            _abiTypeFactories.Add(abiTypeFactory);
        }

        public string LoadContract(Type type)
        {
            using var reader = LoadResource(type);
            return reader.ReadToEnd();
        }

        public string Serialize(AbiDefinition contract)
        {
            return JsonSerializer.Serialize(contract, _op);
        }

        private static StreamReader LoadResource(Type type)
        {
            var jsonResource = type.FullName.Replace("+", ".") + ".json";
#if DEBUG
            var names = type.Assembly.GetManifestResourceNames();
#endif
            var stream = type.Assembly.GetManifestResourceStream(jsonResource) ?? throw new ArgumentException($"Resource for {jsonResource} not found.");
            return new StreamReader(stream);
        }

        public JsonSerializerOptions GetJsonSerializerSettings()
        {
            JsonSerializerOptions jsonSerializerSettings = new()
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            jsonSerializerSettings.Converters.Add(new AbiDefinitionConverter());
            jsonSerializerSettings.Converters.Add(new AbiEventParameterConverter(_abiTypeFactories));
            jsonSerializerSettings.Converters.Add(new AbiParameterConverter(_abiTypeFactories));
            jsonSerializerSettings.Converters.Add(new JsonStringEnumConverter(new LowerCaseNamingPolicy()));
            jsonSerializerSettings.Converters.Add(new AbiTypeConverter());
            return jsonSerializerSettings;
        }
    }
}
