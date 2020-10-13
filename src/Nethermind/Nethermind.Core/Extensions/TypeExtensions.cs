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
                throw new NotSupportedException("This method is only allowed to use on interface types");
            }

            TypeDiscovery typeDiscovery = new TypeDiscovery();
            Type[] derivedInterfaces = interfaceType.GetInterfaces();
            IEnumerable<Type> implementations = typeDiscovery.FindNethermindTypes(interfaceType).Where(i => i.IsClass);

            foreach(Type implementation in implementations)
            {
                List<Type> interfaces = implementation.GetInterfaces().ToList();
                foreach(Type derivedInterface in derivedInterfaces)
                {
                    interfaces.Remove(derivedInterface);
                }

                if(interfaces.Contains(interfaceType) && interfaces.Count() == 1)
                {
                    return implementation;
                }
            }

            return null;
        }
    }
}