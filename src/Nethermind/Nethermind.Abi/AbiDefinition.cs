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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Abi
{
    public class AbiDefinition
    {
        private readonly List<AbiFunctionDescription> _constructors = new List<AbiFunctionDescription>();
        private readonly Dictionary<string, AbiFunctionDescription> _functions = new Dictionary<string, AbiFunctionDescription>();
        private readonly Dictionary<string, AbiEventDescription> _events = new Dictionary<string, AbiEventDescription>();
        private readonly List<AbiBaseDescription> _items = new List<AbiBaseDescription>();

        public IReadOnlyList<AbiFunctionDescription> Constructors => _constructors;
        public IReadOnlyDictionary<string, AbiFunctionDescription> Functions => _functions;
        public IReadOnlyDictionary<string, AbiEventDescription> Events => _events;
        public IReadOnlyList<AbiBaseDescription> Items => _items;

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

        public AbiFunctionDescription GetFunction(string name, bool camelCase = true) => _functions[camelCase ? GetFunctionName(name) : name];

        public string GetFunctionName(string name) => char.IsUpper(name[0]) ? Char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
    }
}