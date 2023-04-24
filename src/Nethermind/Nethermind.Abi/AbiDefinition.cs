// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastEnumUtility;

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    [JsonConverter(typeof(AbiDefinitionConverter))]
    public class AbiDefinition
    {
        private readonly List<AbiFunctionDescription> _constructors = new();
        private readonly Dictionary<string, AbiFunctionDescription> _functions = new();
        private readonly Dictionary<string, AbiEventDescription> _events = new();
        private readonly Dictionary<string, AbiErrorDescription> _errors = new();
        private readonly List<AbiBaseDescription> _items = new();

        public byte[]? Bytecode { get; private set; }
        public byte[]? DeployedBytecode { get; private set; }
        public IReadOnlyList<AbiFunctionDescription> Constructors => _constructors;
        public IReadOnlyDictionary<string, AbiFunctionDescription> Functions => _functions;
        public IReadOnlyDictionary<string, AbiEventDescription> Events => _events;
        public IReadOnlyDictionary<string, AbiErrorDescription> Errors => _errors;
        public IReadOnlyList<AbiBaseDescription> Items => _items;
        public string Name { get; set; } = string.Empty;

        public void SetBytecode(byte[] bytecode)
        {
            Bytecode = bytecode;
        }

        public void SetDeployedBytecode(byte[] deployedBytecode)
        {
            DeployedBytecode = deployedBytecode;
        }

        public void Add(AbiFunctionDescription function)
        {
            if (function.Type == AbiDescriptionType.Constructor)
            {
                _constructors.Add(function);
            }
            else
            {
                _functions.Add(function.Name, function);
            }

            _items.Add(function);
        }

        public void Add(AbiEventDescription @event)
        {
            _events.Add(@event.Name, @event);
            _items.Add(@event);
        }

        public void Add(AbiErrorDescription @error)
        {
            _errors.Add(@error.Name, @error);
            _items.Add(@error);
        }

        public AbiFunctionDescription GetFunction(string name, bool camelCase = true) => _functions[camelCase ? GetName(name) : name];
        public AbiEventDescription GetEvent(string name, bool camelCase = false) => _events[camelCase ? GetName(name) : name];
        public AbiErrorDescription GetError(string name, bool camelCase = false) => _errors[camelCase ? GetName(name) : name];

        public static string GetName(string name) => char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name[1..] : name;
    }
}


namespace Nethermind.Blockchain.Contracts.Json
{
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
            JsonElement topLevelToken, abiToken;
            topLevelToken = document.RootElement;
            if (topLevelToken.ValueKind == JsonValueKind.Object)
            {
                abiToken = topLevelToken.GetProperty("abi"u8);
                if (topLevelToken.TryGetProperty("bytecode"u8, out JsonElement bytecodeBase64))
                    value.SetBytecode(Bytes.FromHexString(bytecodeBase64.GetString()!));
                if (topLevelToken.TryGetProperty("deployedBytecode"u8, out JsonElement deployedBytecodeBase64))
                    value.SetDeployedBytecode(Bytes.FromHexString(deployedBytecodeBase64.GetString()!));
            }
            else
            {
                abiToken = topLevelToken;
            }
            foreach (JsonElement definitionToken in abiToken.EnumerateArray())
            {
                if (!definitionToken.TryGetProperty("type"u8, out JsonElement typeToken))
                    continue;
                AbiDescriptionType type = FastEnum.Parse<AbiDescriptionType>(typeToken.GetString(), true);
                switch (type)
                {
                    case AbiDescriptionType.Event:
                        AbiEventDescription? eventDescription = definitionToken.Deserialize<AbiEventDescription>(op);
                        if (eventDescription != null)
                            value.Add(eventDescription);
                        break;
                    case AbiDescriptionType.Error:
                        AbiErrorDescription? errorDescription = definitionToken.Deserialize<AbiErrorDescription>(op);
                        if (errorDescription != null)
                            value.Add(errorDescription);
                        break;
                    default:
                        AbiFunctionDescription? functionDescription = definitionToken.Deserialize<AbiFunctionDescription>(op);
                        if (functionDescription != null)
                            value.Add(functionDescription);
                        break;
                }
            }
            return value;
        }
    }
}
