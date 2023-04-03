// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        private readonly IList<IAbiTypeFactory> _abiTypeFactories = new List<IAbiTypeFactory>();

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

        private JsonSerializerSettings GetJsonSerializerSettings()
        {
            var jsonSerializerSettings = new JsonSerializerSettings();
            jsonSerializerSettings.Converters.Add(new AbiDefinitionConverter());
            jsonSerializerSettings.Converters.Add(new AbiEventParameterConverter(_abiTypeFactories));
            jsonSerializerSettings.Converters.Add(new AbiParameterConverter(_abiTypeFactories));
            jsonSerializerSettings.Converters.Add(new StringEnumConverter() { NamingStrategy = new LowerCaseNamingStrategy() });
            jsonSerializerSettings.Converters.Add(new AbiTypeConverter());
            jsonSerializerSettings.Formatting = Formatting.Indented;
            jsonSerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            return jsonSerializerSettings;
        }
    }
}
