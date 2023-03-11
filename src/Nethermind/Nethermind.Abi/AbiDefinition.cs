// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Abi
{
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

        public static string GetName(string name) => char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
    }
}
