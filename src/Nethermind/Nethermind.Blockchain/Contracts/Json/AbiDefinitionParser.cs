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
using System.IO;
using System.Text;
using Nethermind.Abi;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Nethermind.Blockchain.Contracts.Json
{
    public class AbiDefinitionParser : IAbiDefinitionParser
    {
        private readonly JsonSerializer _serializer;

        public AbiDefinitionParser()
        {
            _serializer = JsonSerializer.CreateDefault(GetJsonSerializerSettings());
        }

        public AbiDefinition Parse(string json, string name = null)
        {
            using var reader = new StringReader(json);
            return Parse(reader, name);
        }

        public AbiDefinition Parse(Type type)
        {
            using var reader = LoadResource(type);
            return Parse(reader, type.Name);
        }

        public string LoadContract(Type type)
        {
            using var reader = LoadResource(type);
            return reader.ReadToEnd();
        }

        public string Serialize(AbiDefinition contract)
        {
            var builder = new StringBuilder();
            using var writer = new StringWriter(builder);
            _serializer.Serialize(writer, contract);
            return builder.ToString();
        }

        private AbiDefinition Parse(TextReader textReader, string name)
        {
            using var reader = new JsonTextReader(textReader);
            var definition = _serializer.Deserialize<AbiDefinition>(reader);
            definition.Name = name;
            return definition;
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

        private static JsonSerializerSettings GetJsonSerializerSettings()
        {
            var jsonSerializerSettings = new JsonSerializerSettings();
            jsonSerializerSettings.Converters.Add(new AbiDefinitionConverter());
            jsonSerializerSettings.Converters.Add(new AbiEventParameterConverter());
            jsonSerializerSettings.Converters.Add(new AbiParameterConverter());
            jsonSerializerSettings.Converters.Add(new StringEnumConverter() {NamingStrategy = new LowerCaseNamingStrategy()});
            jsonSerializerSettings.Converters.Add(new AbiTypeConverter());
            jsonSerializerSettings.Formatting = Formatting.Indented;
            jsonSerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            return jsonSerializerSettings;
        }
    }
}
