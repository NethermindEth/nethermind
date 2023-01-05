// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public AbiTuple(params AbiType[] elements) : this(elements, null)
        {

        }

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
            (object[] arguments, int movedPosition) = DecodeSequence(_elements.Length, _elements, data, packed, position);
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
                byte[][] encodedItems = EncodeSequence(_elements.Length, _elements, GetEnumerable(input), packed);
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType => _type.Value;

        private static Type GetCSharpType(AbiType[] elements)
        {
            Type genericType = Type.GetType("System.ValueTuple`" + elements.Length)!;
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
            (object[] arguments, int movedPosition) = DecodeSequence(_elements.Length, _elements, data, packed, position);

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
                byte[][] encodedItems = EncodeSequence(_elements.Length, _elements, values, packed);
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
