// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    public partial class AbiDefinitionParser : IAbiDefinitionParser
    {
        private readonly IList<IAbiTypeFactory> _abiTypeFactories = new List<IAbiTypeFactory>();

        public AbiDefinitionParser()
        {
        }

        public AbiDefinition Parse(string json, string name = null)
        {
            AbiDefinition definition = JsonSerializer.Deserialize<AbiDefinition>(json, SourceGenerationContext.Default.AbiDefinition);
            definition.Name = name;
            return definition;
        }

        public AbiDefinition Parse(Type type)
        {
            using var reader = LoadResource(type);
            AbiDefinition definition = JsonSerializer.Deserialize<AbiDefinition>(reader, SourceGenerationContext.Default.AbiDefinition);
            definition.Name = type.Name;
            return definition;
        }

        public void RegisterAbiTypeFactory(IAbiTypeFactory abiTypeFactory)
        {
            _abiTypeFactories.Add(abiTypeFactory);
        }

        public string LoadContract(Type type)
        {
            using var reader = new StreamReader(LoadResource(type));
            return reader.ReadToEnd();
        }

        public string Serialize(AbiDefinition contract)
        {
            return JsonSerializer.Serialize(contract, SourceGenerationContext.Default.AbiDefinition);
        }

        private static Stream LoadResource(Type type)
        {
            var jsonResource = type.FullName.Replace("+", ".") + ".json";
#if DEBUG
            var names = type.Assembly.GetManifestResourceNames();
#endif
            var stream = type.Assembly.GetManifestResourceStream(jsonResource) ?? throw new ArgumentException($"Resource for {jsonResource} not found.");
            return stream;
        }

        [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        [JsonSerializable(typeof(AbiDefinition))]
        [JsonSerializable(typeof(AbiFunctionDescription))]
        [JsonSerializable(typeof(AbiParameter))]
        [JsonSerializable(typeof(AbiEventParameter))]
        [JsonSerializable(typeof(AbiBaseDescription))]
        [JsonSerializable(typeof(AbiEventDescription))]
        [JsonSerializable(typeof(AbiFunctionDescription))]
        [JsonSerializable(typeof(AbiErrorDescription))]
        internal partial class SourceGenerationContext : JsonSerializerContext
        {
        }
    }
}
