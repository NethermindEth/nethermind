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
using System.Runtime.Loader;
using System.Threading;

namespace Nethermind.Core
{
    public class TypeDiscovery
    {
        private HashSet<Assembly> _nethermindAssemblies = new();

        private int _allLoaded;

        private void LoadAll()
        {
            if (Interlocked.CompareExchange(ref _allLoaded, 1, 0) == 0)
            {
                List<Assembly> loadedAssemblies;
                do
                {
                    loadedAssemblies = AssemblyLoadContext.Default.Assemblies.ToList();
                } while (LoadOnce(loadedAssemblies) != 0);

                foreach (Assembly assembly in loadedAssemblies.Where(a => a.FullName?.Contains("Nethermind") ?? false))
                {
                    _nethermindAssemblies.Add(assembly);
                }
            }
        }

        private int LoadOnce(List<Assembly> loadedAssemblies)
        {
            // can potentially use https://docs.microsoft.com/en-us/dotnet/standard/assembly/unloadability

            int loaded = 0;

            var missingRefs = loadedAssemblies
                .SelectMany(x => x.GetReferencedAssemblies())
                .Distinct()
                .Where(a => a.Name?.Contains("Nethermind") ?? false);

            foreach (AssemblyName missingRef in missingRefs)
            {
                if (loadedAssemblies.All(a => a.FullName != missingRef.FullName))
                {
                    AssemblyLoadContext.Default.LoadFromAssemblyName(missingRef);
                    loaded++;
                }
            }

            return loaded;
        }

        public IEnumerable<Type> FindNethermindTypes(Type baseType)
        {
            LoadAll();

            return _nethermindAssemblies
                .SelectMany(a => (a?.IsDynamic ?? false ? Array.Empty<Type>() : a?.GetExportedTypes())?
                    .Where(t => baseType.IsAssignableFrom(t) && baseType != t) ?? Array.Empty<Type>());
        }

        public IEnumerable<Type> FindNethermindTypes(string typeName)
        {
            LoadAll();

            return _nethermindAssemblies
                .SelectMany(a => (a?.IsDynamic ?? false ? Array.Empty<Type>() : a?.GetExportedTypes())?
                    .Where(t => t.Name == typeName) ?? Array.Empty<Type>());
        }
    }
}
