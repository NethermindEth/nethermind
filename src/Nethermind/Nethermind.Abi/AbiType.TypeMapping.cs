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
