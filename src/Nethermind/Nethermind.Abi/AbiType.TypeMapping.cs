// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public partial class AbiType
    {
        private static readonly IDictionary<Type, AbiType> _typeMappings = new Dictionary<Type, AbiType>();

        protected static AbiType GetForCSharpType(Type type)
        {
            if (_typeMappings.TryGetValue(type, out AbiType? abiTYpe))
            {
                return abiTYpe;
            }
            else if (type.IsArray)
            {
                if (type == typeof(byte[]))
                {
                    return DynamicBytes;
                }

                Type elementType = type.GetElementType()!;
                return new AbiArray(GetForCSharpType(elementType));
            }
            else if (type.IsValueTuple())
            {
                Type[] subTypes = type.GetGenericArguments();
                AbiType[] elements = new AbiType[subTypes.Length];
                for (int i = 0; i < subTypes.Length; i++)
                {
                    elements[i] = GetForCSharpType(subTypes[i]);
                }

                return new AbiTuple(elements);
            }
            else
            {
                throw new NotSupportedException($"Type {type} doesn't have mapped {nameof(AbiType)}");
            }
        }

        protected static void RegisterMapping<T>(AbiType abiType)
        {
            _typeMappings[typeof(T)] = abiType;
        }

        static AbiType()
        {
            AbiType type = AbiAddress.Instance;
            type = AbiBool.Instance;
            type = AbiDynamicBytes.Instance;
            type = AbiInt.Int8;
            type = AbiString.Instance;
            type = AbiUInt.UInt8;
        }
    }
}
