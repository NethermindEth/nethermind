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

            TypeDiscovery typeDiscovery = new();
            Type[] baseInterfaces = interfaceType.GetInterfaces();
            IEnumerable<Type> implementations = typeDiscovery.FindNethermindTypes(interfaceType).Where(i => i.IsClass);

            foreach(Type implementation in implementations)
            {
                List<Type> interfaces = implementation.GetInterfaces().ToList();

                interfaces.RemoveAll(i => baseInterfaces.Contains(i));

                if(interfaces.Contains(interfaceType) && interfaces.Count() == 1)
                {
                    return implementation;
                }
            }

            throw new InvalidOperationException($"Couldn't find direct implementation of {interfaceType} interface");
        }
    }
}