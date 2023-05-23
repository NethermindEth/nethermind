// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.Extensions
{
    public static class TypeExtensions
    {
        public static Type GetDirectInterfaceImplementation(this Type interfaceType)
        {
            if (!interfaceType.IsInterface)
            {
                throw new NotSupportedException($"GetDirectInterfaceImplementation method is only allowed to use on interface types, got {interfaceType} instead");
            }

            Type[] baseInterfaces = interfaceType.GetInterfaces();
            IEnumerable<Type> implementations = TypeDiscovery.FindNethermindTypes(interfaceType).Where(i => i.IsClass);

            foreach (Type implementation in implementations)
            {
                List<Type> interfaces = implementation.GetInterfaces().ToList();

                interfaces.RemoveAll(i => baseInterfaces.Contains(i));

                if (interfaces.Contains(interfaceType))
                {
                    return implementation;
                }
            }

            throw new InvalidOperationException($"Couldn't find direct implementation of {interfaceType} interface");
        }

        private static readonly ISet<Type> _valueTupleTypes = new HashSet<Type>(
            new Type[] {
                typeof(ValueTuple<>),
                typeof(ValueTuple<,>),
                typeof(ValueTuple<,,>),
                typeof(ValueTuple<,,,>),
                typeof(ValueTuple<,,,,>),
                typeof(ValueTuple<,,,,,>),
                typeof(ValueTuple<,,,,,,>),
                typeof(ValueTuple<,,,,,,,>)
            }
        );

        public static bool IsValueTuple(this Type type) =>
            type.IsGenericType && _valueTupleTypes.Contains(type.GetGenericTypeDefinition());

        public static bool CanBeAssignedNull(this Type type) =>
            !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

        public static bool CannotBeAssignedNull(this Type type) =>
            type.IsValueType && Nullable.GetUnderlyingType(type) is null;
    }
}
