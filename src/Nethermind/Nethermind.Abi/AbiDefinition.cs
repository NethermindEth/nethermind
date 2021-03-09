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

using System.Collections.Generic;

namespace Nethermind.Abi
{
    public class AbiDefinition
    {
        private readonly List<AbiFunctionDescription> _constructors = new();
        private readonly Dictionary<string, AbiFunctionDescription> _functions = new();
        private readonly Dictionary<string, AbiEventDescription> _events = new();
        private readonly List<AbiBaseDescription> _items = new();

        public byte[]? Bytecode { get; private set; }
        public IReadOnlyList<AbiFunctionDescription> Constructors => _constructors;
        public IReadOnlyDictionary<string, AbiFunctionDescription> Functions => _functions;
        public IReadOnlyDictionary<string, AbiEventDescription> Events => _events;
        public IReadOnlyList<AbiBaseDescription> Items => _items;
        public string Name { get; set; } = string.Empty;

        public void SetBytecode(byte[] bytecode)
        {
            Bytecode = bytecode;
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

        public AbiFunctionDescription GetFunction(string name, bool camelCase = true) => _functions[camelCase ? GetName(name) : name];
        public AbiEventDescription GetEvent(string name, bool camelCase = false) => _events[camelCase ? GetName(name) : name];

        public static string GetName(string name) => char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
    }
}
