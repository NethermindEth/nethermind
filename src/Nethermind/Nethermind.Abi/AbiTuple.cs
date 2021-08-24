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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiTuple : AbiType
    {
        private readonly Lazy<Type> _type; 
        
        public AbiTuple(IReadOnlyDictionary<string, AbiType> elements)
        {
            if (elements.Count > 8)
            {
                throw new ArgumentException($"Too many tuple items {elements.Count}, max is 8. Please use custom type instead.", nameof(elements));
            }
            
            Elements = elements;
            Name = $"tuple({string.Join(", ", elements.Values.Select(v => v.Name))})";
            _type = new Lazy<Type>(() => GetCSharpType(Elements));
        }

        public override string Name { get; }
        
        public IReadOnlyDictionary<string, AbiType> Elements { get; }
        
        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            object[] values = new object[Elements.Count];
            int i = 0;
            foreach (AbiType type in Elements.Values)
            {
                (values[i], position) = type.Decode(data, position, packed);
                i++;
            }

            return (Activator.CreateInstance(CSharpType, values), position)!;
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is ITuple input && input.Length == Elements.Count)
            {
                byte[][] encodedItems = new byte[Elements.Count][];
                int i = 0;
                foreach (AbiType type in Elements.Values)
                {
                    encodedItems[i] = type.Encode(input[i], packed);
                    i++;
                }
                
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType => _type.Value;

        private static Type GetCSharpType(IReadOnlyDictionary<string, AbiType> elements)
        {
            Type genericType = Type.GetType("System.ValueTuple`" + (elements.Count <= 8 ? elements.Count : 8))!;
            Type[] typeArguments = elements.Values.Select(v => v.CSharpType).ToArray();
            return genericType.MakeGenericType(typeArguments);
        }
    }

    public class AbiTuple<T> : AbiType where T : new()
    {
        private readonly PropertyInfo[] _properties;
        public override string Name { get; }

        public AbiTuple()
        {
            _properties = typeof(T).GetProperties();
            Name = $"tuple({string.Join(", ", _properties.Select(GetAbiType))})";
        }
        
        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            T item = new T();
            for (int i = 0; i < _properties.Length; i++)
            {
                PropertyInfo property = _properties[i];
                AbiType abiType = GetAbiType(property);
                object value;
                (value, position) = abiType.Decode(data, position, packed);
                property.SetValue(item, value);
            }

            return (item, position);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is T item)
            {
                byte[][] encodedItems = new byte[_properties.Length][];
                for (int i = 0; i < _properties.Length; i++)
                {
                    PropertyInfo property = _properties[i];
                    object? value = property.GetValue(item);
                    AbiType abiType = GetAbiType(property);
                    encodedItems[i] = abiType.Encode(value, packed);
                }
                
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType => typeof(T);
        
        private AbiType GetAbiType(PropertyInfo property)
        {
            AbiTypeMappingAttribute? abiTypeMappingAttribute = property.GetCustomAttribute<AbiTypeMappingAttribute>();
            return abiTypeMappingAttribute is not null ? abiTypeMappingAttribute.AbiType : GetForCSharpType(property.PropertyType);
        }
    }
}
