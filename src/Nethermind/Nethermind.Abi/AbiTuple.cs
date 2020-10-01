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
// 

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Abi
{
    public class AbiTuple : AbiType
    {
        private readonly Lazy<Type> _type; 
        
        public AbiTuple(IReadOnlyDictionary<string, AbiType> elements)
        {
            Elements = elements;
            Name = $"tuple({string.Join(", ", elements.Values.Select(v => v.Name))})";
            _type = new Lazy<Type>(() => GetCSharpType(Elements));
        }

        public override string Name { get; }
        
        public IReadOnlyDictionary<string, AbiType> Elements { get; }
        
        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            throw new NotImplementedException();
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            throw new NotImplementedException();
        }

        public override Type CSharpType => _type.Value;

        private static Type GetCSharpType(IReadOnlyDictionary<string, AbiType> elements)
        {
            Type genericType = Type.GetType("System.ValueTuple`" + elements.Count)!;
            Type[] typeArguments = elements.Values.Select(v => v.CSharpType).ToArray();
            return genericType.MakeGenericType(typeArguments);
        }
    }
}
