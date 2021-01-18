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

using System;
using System.Diagnostics;

namespace Nethermind.HashLib.Extensions
{
    [DebuggerStepThrough]
    internal static class TypeExtensions
    {
        public static bool IsDerivedFrom(this Type a_type, Type a_baseType)
        {
            Debug.Assert(a_type != null);
            Debug.Assert(a_baseType != null);
            Debug.Assert(a_type.IsClass);
            Debug.Assert(a_baseType.IsClass);

            return a_baseType.IsAssignableFrom(a_type);
        }

        public static bool IsImplementInterface(this Type a_type, Type a_interfaceType)
        {
            Debug.Assert(a_type != null);
            Debug.Assert(a_interfaceType != null);
            Debug.Assert(a_type.IsClass || a_type.IsInterface || a_type.IsValueType);
            Debug.Assert(a_interfaceType.IsInterface);

            return a_interfaceType.IsAssignableFrom(a_type);
        }
    }
}
