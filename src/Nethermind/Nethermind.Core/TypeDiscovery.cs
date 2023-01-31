// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                .GroupBy(a => a.FullName)
                .Select(g => g.First())
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
