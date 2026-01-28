// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;

namespace JitAsm;

internal sealed class MethodResolver(Assembly assembly)
{
    private static readonly Dictionary<string, Type> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["char"] = typeof(char),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["decimal"] = typeof(decimal),
        ["string"] = typeof(string),
        ["object"] = typeof(object),
        ["void"] = typeof(void),
        ["nint"] = typeof(nint),
        ["nuint"] = typeof(nuint),
    };

    public MethodInfo? ResolveMethod(string? typeName, string methodName, string? typeParams, string? classTypeParams = null)
    {
        var candidates = new List<(Type Type, MethodInfo Method)>();

        if (typeName is not null)
        {
            // Search specific type
            var type = ResolveType(typeName);
            if (type is null)
            {
                return null;
            }

            // If the type is a generic type definition and we have class type params, construct the concrete type
            if (type.IsGenericTypeDefinition && classTypeParams is not null)
            {
                type = MakeGenericType(type, classTypeParams);
                if (type is null)
                {
                    return null;
                }
            }

            var methods = FindMethods(type, methodName);
            candidates.AddRange(methods.Select(m => (type, m)));

            // Also search base types if no methods found (for inherited methods)
            if (candidates.Count == 0)
            {
                var baseType = type.BaseType;
                while (baseType is not null && candidates.Count == 0)
                {
                    methods = FindMethods(baseType, methodName, includeInherited: true);
                    candidates.AddRange(methods.Select(m => (baseType, m)));
                    baseType = baseType.BaseType;
                }
            }
        }
        else
        {
            // Search all types
            foreach (var type in assembly.GetTypes())
            {
                var searchType = type;

                // If the type is a generic type definition and we have class type params, try to construct it
                if (type.IsGenericTypeDefinition && classTypeParams is not null)
                {
                    var typeParamCount = classTypeParams.Split(',').Length;
                    if (type.GetGenericArguments().Length == typeParamCount)
                    {
                        var constructed = MakeGenericType(type, classTypeParams);
                        if (constructed is not null)
                        {
                            searchType = constructed;
                        }
                    }
                }

                var methods = FindMethods(searchType, methodName);
                candidates.AddRange(methods.Select(m => (searchType, m)));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        bool verbose = Environment.GetEnvironmentVariable("JITASM_VERBOSE") == "1";
        if (verbose)
        {
            Console.Error.WriteLine($"[DEBUG] Candidates before filtering: {candidates.Count}");
            foreach (var c in candidates)
            {
                Console.Error.WriteLine($"[DEBUG]   Method: {c.Method.Name}, IsGenericMethodDefinition: {c.Method.IsGenericMethodDefinition}, GenericArgCount: {c.Method.GetGenericArguments().Length}");
            }
        }

        // If type params are specified, filter to only methods with matching generic param count
        if (typeParams is not null)
        {
            var genericParamCount = typeParams.Split(',').Length;
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] Looking for generic methods with {genericParamCount} type params");

            var genericMatches = candidates.Where(c => c.Method.IsGenericMethodDefinition &&
                c.Method.GetGenericArguments().Length == genericParamCount).ToList();

            if (verbose)
                Console.Error.WriteLine($"[DEBUG] Generic matches: {genericMatches.Count}");

            if (genericMatches.Count > 0)
            {
                candidates = genericMatches;
            }
        }
        else
        {
            // Prefer non-generic methods if no type params specified
            var nonGeneric = candidates.Where(c => !c.Method.IsGenericMethodDefinition).ToList();
            if (nonGeneric.Count > 0)
            {
                candidates = nonGeneric;
            }
        }

        if (candidates.Count == 0)
        {
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] No candidates after filtering");
            return null;
        }

        if (verbose)
            Console.Error.WriteLine($"[DEBUG] Final candidates: {candidates.Count}, calling MakeGenericIfNeeded");

        // If there's only one candidate, use it
        if (candidates.Count == 1)
        {
            return MakeGenericIfNeeded(candidates[0].Method, typeParams);
        }

        // Return first match
        return MakeGenericIfNeeded(candidates[0].Method, typeParams);
    }

    private Type? MakeGenericType(Type genericTypeDefinition, string classTypeParams)
    {
        var typeNames = classTypeParams.Split(',', StringSplitOptions.TrimEntries);
        var types = new Type[typeNames.Length];

        bool verbose = Environment.GetEnvironmentVariable("JITASM_VERBOSE") == "1";

        for (int i = 0; i < typeNames.Length; i++)
        {
            var resolved = ResolveTypeParam(typeNames[i]);
            if (resolved is null)
            {
                if (verbose)
                    Console.Error.WriteLine($"[DEBUG] Failed to resolve type param: {typeNames[i]}");
                return null;
            }
            types[i] = resolved;
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] Resolved type param {typeNames[i]} to {resolved.FullName}");
        }

        try
        {
            var result = genericTypeDefinition.MakeGenericType(types);
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] Constructed generic type: {result.FullName}");
            return result;
        }
        catch (Exception ex)
        {
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] MakeGenericType failed: {ex.Message}");
            return null;
        }
    }

    private Type? ResolveType(string typeName)
    {
        // Try direct lookup
        var type = assembly.GetType(typeName);
        if (type is not null) return type;

        // Try with assembly name prefix removed if present
        var types = assembly.GetTypes();

        // Try exact match on FullName
        type = types.FirstOrDefault(t => t.FullName == typeName);
        if (type is not null) return type;

        // Try match on Name only
        type = types.FirstOrDefault(t => t.Name == typeName);
        if (type is not null) return type;

        // Try matching generic types by base name (without the `N suffix)
        // e.g., "TransactionProcessorBase" should match "TransactionProcessorBase`1"
        type = types.FirstOrDefault(t => t.IsGenericTypeDefinition &&
            (t.FullName?.StartsWith(typeName + "`") == true ||
             t.Name.StartsWith(typeName + "`")));
        if (type is not null) return type;

        // Also try matching with the ` syntax - the type might be specified as TypeName`1
        if (typeName.Contains('`'))
        {
            type = types.FirstOrDefault(t => t.FullName == typeName || t.Name == typeName);
            if (type is not null) return type;
        }

        // Try nested types
        foreach (var t in types)
        {
            if (t.FullName is not null && typeName.StartsWith(t.FullName + "+"))
            {
                var nestedName = typeName[(t.FullName.Length + 1)..];
                var nested = t.GetNestedType(nestedName, BindingFlags.Public | BindingFlags.NonPublic);
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    private static IEnumerable<MethodInfo> FindMethods(Type type, string methodName, bool includeInherited = false)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static;

        if (!includeInherited)
        {
            flags |= BindingFlags.DeclaredOnly;
        }

        bool verbose = Environment.GetEnvironmentVariable("JITASM_VERBOSE") == "1";
        var methods = type.GetMethods(flags).Where(m => m.Name == methodName).ToList();
        if (verbose)
        {
            Console.Error.WriteLine($"[DEBUG] FindMethods on {type.FullName} for '{methodName}': found {methods.Count} methods");
            if (methods.Count == 0)
            {
                // List some methods to help debug
                var allMethods = type.GetMethods(flags).Where(m => m.Name.Contains("Evm") || m.Name.Contains("Execute")).Take(10);
                Console.Error.WriteLine($"[DEBUG] Sample methods containing 'Evm' or 'Execute': {string.Join(", ", allMethods.Select(m => m.Name))}");
            }
        }

        return methods;
    }

    private MethodInfo? MakeGenericIfNeeded(MethodInfo method, string? typeParams)
    {
        bool verbose = Environment.GetEnvironmentVariable("JITASM_VERBOSE") == "1";

        if (typeParams is null || !method.IsGenericMethodDefinition)
        {
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] MakeGenericIfNeeded: returning method as-is (typeParams={typeParams}, IsGenericMethodDefinition={method.IsGenericMethodDefinition})");
            return method;
        }

        var typeNames = typeParams.Split(',', StringSplitOptions.TrimEntries);
        var types = new Type[typeNames.Length];

        if (verbose)
            Console.Error.WriteLine($"[DEBUG] MakeGenericIfNeeded: resolving {typeNames.Length} type params: {string.Join(", ", typeNames)}");

        for (int i = 0; i < typeNames.Length; i++)
        {
            var resolved = ResolveTypeParam(typeNames[i]);
            if (resolved is null)
            {
                if (verbose)
                    Console.Error.WriteLine($"[DEBUG] MakeGenericIfNeeded: failed to resolve type param '{typeNames[i]}'");
                return null;
            }
            types[i] = resolved;
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] MakeGenericIfNeeded: resolved '{typeNames[i]}' to {resolved.FullName}");
        }

        try
        {
            var result = method.MakeGenericMethod(types);
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] MakeGenericIfNeeded: success, created {result}");
            return result;
        }
        catch (Exception ex)
        {
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] MakeGenericIfNeeded: MakeGenericMethod failed: {ex.Message}");
            return null;
        }
    }

    private Type? ResolveTypeParam(string typeName)
    {
        // Check aliases first
        if (TypeAliases.TryGetValue(typeName, out var aliasType))
        {
            return aliasType;
        }

        // Try the target assembly
        var type = ResolveType(typeName);
        if (type is not null) return type;

        // Try referenced assemblies
        foreach (var refName in assembly.GetReferencedAssemblies())
        {
            try
            {
                var refAssembly = Assembly.Load(refName);
                type = refAssembly.GetType(typeName);
                if (type is not null) return type;

                // Try by short name
                type = refAssembly.GetTypes().FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
                if (type is not null) return type;
            }
            catch
            {
                // Skip assemblies that can't be loaded
            }
        }

        // Try Type.GetType as last resort
        return Type.GetType(typeName);
    }

    public IEnumerable<MethodInfo> FindAllMethods(string? typeName, string methodName)
    {
        if (typeName is not null)
        {
            var type = ResolveType(typeName);
            if (type is not null)
            {
                return FindMethods(type, methodName);
            }
            return [];
        }

        var results = new List<MethodInfo>();
        foreach (var type in assembly.GetTypes())
        {
            results.AddRange(FindMethods(type, methodName));
        }
        return results;
    }
}
