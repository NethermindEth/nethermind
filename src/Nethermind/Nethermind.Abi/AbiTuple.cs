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
        private readonly AbiType[] _elements;
        private readonly string[]? _names;
        private readonly Lazy<Type> _type; 
        
        public AbiTuple(AbiType[] elements, string[]? names = null)
        {
            if (elements.Length > 8)
            {
                throw new ArgumentException($"Too many tuple items {elements.Length}, max is 8. Please use custom type instead.", nameof(elements));
            }

            _elements = elements;
            _names = names;
            Name = $"({string.Join(",", _elements.Select(v => v.Name))})";
            _type = new Lazy<Type>(() => GetCSharpType(_elements));
            IsDynamic = _elements.Any(p => p.IsDynamic);
        }

        public override string Name { get; }

        public override bool IsDynamic { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (object[] arguments, int movedPosition) = DecodeSequence(_elements, data, packed, position);
            return (Activator.CreateInstance(CSharpType, arguments), movedPosition)!;
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            IEnumerable<object?> GetEnumerable(ITuple tuple)
            {
                for (int i = 0; i < tuple.Length; i++)
                {
                    yield return tuple[i];
                }
            }
            
            if (arg is ITuple input && input.Length == _elements.Length)
            {
                byte[][] encodedItems = EncodeSequence(_elements, GetEnumerable(input), packed);
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType => _type.Value;

        private static Type GetCSharpType(AbiType[] elements)
        {
            Type genericType = Type.GetType("System.ValueTuple`" + (elements.Length <= 8 ? elements.Length : 8))!;
            Type[] typeArguments = elements.Select(v => v.CSharpType).ToArray();
            return genericType.MakeGenericType(typeArguments);
        }
    }

    public class AbiTuple<T> : AbiType where T : new()
    {
        private readonly PropertyInfo[] _properties;
        private readonly AbiType[] _elements;
        public override string Name { get; }
        public override bool IsDynamic { get; }

        public AbiTuple()
        {
            _properties = typeof(T).GetProperties();
            _elements = _properties.Select(GetAbiType).ToArray();
            Name = $"({string.Join(",", _elements.AsEnumerable())})";
            IsDynamic = _elements.Any(p => p.IsDynamic);
        }
        
        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (object[] arguments, int movedPosition) = DecodeSequence(_elements, data, packed, position);

            object item = new T();
            for (int i = 0; i < _properties.Length; i++)
            {
                PropertyInfo property = _properties[i];
                property.SetValue(item, arguments[i]);
            }

            return (item, movedPosition);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is T item)
            {
                IEnumerable<object?> values = _properties.Select(p => p.GetValue(item));
                byte[][] encodedItems = EncodeSequence(_elements, values, packed);
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType => typeof(T);

        private static AbiType GetAbiType(PropertyInfo property)
        {
            AbiTypeMappingAttribute? abiTypeMappingAttribute = property.GetCustomAttribute<AbiTypeMappingAttribute>();
            return abiTypeMappingAttribute is not null ? abiTypeMappingAttribute.AbiType : GetForCSharpType(property.PropertyType);
        }
    }
}
