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
    public static class TypeDiscovery
    {
        private static readonly HashSet<Assembly> _assembliesWithNethermindTypes = new();
        private static readonly object _lock = new();
        private static int _allLoaded;
        private static Type? _pluginType;

        public static void Initialize(Type? pluginType = null)
        {
            // Early return if initialised
            if (Volatile.Read(ref _allLoaded) == 1) return;

            if (pluginType is not null)
            {
                _pluginType = pluginType;
            }

            LoadAllImpl();
        }

        private static void LoadAllImpl()
        {
            lock (_lock)
            {
                // Early return if initialised while waiting for lock
                if (Volatile.Read(ref _allLoaded) == 1) return;

                List<Assembly> loadedAssemblies = new(capacity: 48);
                Dictionary<string, Assembly> considered = new();
                foreach (Assembly assembly in AssemblyLoadContext.Default.Assemblies)
                {
                    // Skip null names (shouldn't happen)
                    if (assembly.FullName is null) continue;

                    // Skip top level upstream assemblies that are already loaded
                    // as they won't reference anything in Nethermind
                    if (assembly.FullName.StartsWith("System")
                        || assembly.FullName.StartsWith("Microsoft")
                        || assembly.FullName.StartsWith("NLog")
                        || assembly.FullName.StartsWith("netstandard")
                        || assembly.FullName.StartsWith("TestableIO")
                        || assembly.FullName.StartsWith("Newtonsoft")
                        || assembly.FullName.StartsWith("DotNetty"))
                    {
                        continue;
                    }

                    int commaIndex = assembly.FullName.IndexOf(',');
                    // Skip non full names (shouldn't happen)
                    if (commaIndex <= 0) continue;

                    // Just add the .Name portion as that's what we'll use to compare with AssemblyName
                    // as full name (including version, culture, SN hash) is constructed on the fly
                    // for AssemblyName.FullName so long and allocating
                    string name = assembly.FullName[..commaIndex];
                    if (considered.TryAdd(name, assembly))
                    {
                        loadedAssemblies.Add(assembly);
                    }
                }

                LoadOnce(loadedAssemblies, considered);

                foreach (KeyValuePair<string, Assembly> kv in considered.Where(static kv =>
                             kv.Key.StartsWith("Nethermind") || (_pluginType is not null && FindNethermindBasedTypes(kv.Value, _pluginType).Any())))
                {
                    _assembliesWithNethermindTypes.Add(kv.Value);
                }

                // Mark initialised before releasing lock
                Volatile.Write(ref _allLoaded, 1);
            }
        }

        private static void LoadOnce(List<Assembly> loadedAssemblies, Dictionary<string, Assembly> considered)
        {
            // can potentially use https://docs.microsoft.com/en-us/dotnet/standard/assembly/unloadability

            // Closure capture the dictionary once
            Func<AssemblyName, bool> whereFilter = an => Filter(considered, an);

            List<AssemblyName> missingRefs = loadedAssemblies
                .SelectMany(x => x.GetReferencedAssemblies().Where(whereFilter))
                .ToList();

            for (int i = 0; i < missingRefs.Count; i++)
            {
                AssemblyName missingRef = missingRefs[i];
                if (missingRef.Name is null
                    // Only include new distinct assemblies
                    || considered.ContainsKey(missingRef.Name))
                {
                    continue;
                }

                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(missingRef);
                considered.Add(missingRef.Name, assembly);
                if (assembly is null)
                {
                    // Shouldn't happen (completeness)
                    continue;
                }

                // Find the references of references, filtering out any we've already looked at
                IEnumerable<AssemblyName> newRefs = assembly.GetReferencedAssemblies().Where(whereFilter);

                // Add any extra to end of the list so they will still get picked up from the for loop
                missingRefs.AddRange(newRefs);
            }

            static bool Filter(Dictionary<string, Assembly> considered, AssemblyName an)
            {
                return an.Name is not null
                        && !considered.ContainsKey(an.Name)
                        && an.Name.StartsWith("Nethermind");
            }
        }

        public static IEnumerable<Type> FindNethermindBasedTypes(Type baseType)
        {
            Initialize();

            return FindNethermindBasedTypes(_assembliesWithNethermindTypes, baseType);
        }

        private static IEnumerable<Type> FindNethermindBasedTypes(IEnumerable<Assembly> assemblies, Type baseType)
        {
            Func<Assembly, IEnumerable<Type>> assembliesSelector = a => FindNethermindBasedTypes(a, baseType);

            return assemblies.SelectMany(assembliesSelector);
        }

        private static IEnumerable<Type> FindNethermindBasedTypes(Assembly assembly, Type baseType) =>
            GetExportedTypes(assembly).Where(t => baseType.IsAssignableFrom(t) && baseType != t);

        private static IEnumerable<Type> GetExportedTypes(Assembly? a)
            => a is null || a.IsDynamic ? Array.Empty<Type>() : a.GetExportedTypes();

        public static IEnumerable<Type> FindNethermindBasedTypes(string typeName)
        {
            Initialize();

            Func<Assembly, IEnumerable<Type>> assembliesSelector = a => GetExportedTypes(a)
                .Where(t => t.Name == typeName);

            return _assembliesWithNethermindTypes.SelectMany(assembliesSelector);
        }
    }
}
