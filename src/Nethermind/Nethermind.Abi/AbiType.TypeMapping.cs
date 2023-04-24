// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    using Nethermind.Int256;
    public partial class AbiType
    {
        private static readonly object _registerLock = new();
        private static Dictionary<Type, AbiType> _typeMappings = CreateTypeMappings();

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

        protected static bool IsMappingRegistered<T>()
        {
            return _typeMappings.ContainsKey(typeof(T));
        }

        protected static void RegisterMapping<T>(AbiType abiType)
        {
            lock (_registerLock)
            {
                Dictionary<Type, AbiType> typeMappings = new(_typeMappings)
                {
                    [typeof(T)] = abiType
                };

                _typeMappings = typeMappings;
            }
        }

        private static Dictionary<Type, AbiType> CreateTypeMappings()
        {
            Dictionary<Type, AbiType> typeMappings = new()
            {
                [typeof(bool)] = AbiBool.Instance,
                [typeof(byte)] = AbiUInt.UInt8,
                [typeof(sbyte)] = AbiInt.Int8,
                [typeof(ushort)] = AbiUInt.UInt16,
                [typeof(short)] = AbiInt.Int16,
                [typeof(uint)] = AbiUInt.UInt32,
                [typeof(int)] = AbiInt.Int32,
                [typeof(ulong)] = AbiUInt.UInt64,
                [typeof(long)] = AbiInt.Int64,
                [typeof(UInt256)] = AbiUInt.UInt256,
                [typeof(Int256)] = AbiInt.Int256,
                [typeof(Address)] = AbiAddress.Instance,
                [typeof(string)] = AbiString.Instance,
                [typeof(byte[])] = AbiDynamicBytes.Instance
            };

            return typeMappings;
        }
    }
}
