// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Nethermind.Config;

/// <summary>
/// The configuration files shipped as embedded resources of this assembly, and of plugin assemblies that ship their own
/// (<c>configs/&lt;name&gt;.json</c> resources, added with <see cref="AddFrom"/>). Names of this assembly's configurations
/// take precedence over plugins' names.
/// </summary>
public static class BuiltInConfigs
{
    private const string ResourcePrefix = "configs/";
    private const string Extension = ".json";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (Assembly Assembly, string Resource)> Resources = LoadResourceNames(typeof(BuiltInConfigs).Assembly);

    /// <summary>
    /// The names of the built-in configurations, without the file extension, sorted.
    /// </summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Gate) return GetSortedNames();
        }
    }

    /// <summary>
    /// Opens the built-in configuration with the given name. The name is case-insensitive and can end with <c>.json</c>.
    /// </summary>
    public static Stream? Open(string name)
    {
        lock (Gate)
        {
            return Resources.TryGetValue(TrimExtension(name), out (Assembly Assembly, string Resource) entry)
                ? entry.Assembly.GetManifestResourceStream(entry.Resource)
                : null;
        }
    }

    /// <summary>
    /// Adds the configurations a plugin assembly embeds as <c>configs/&lt;name&gt;.json</c> resources. A name that is
    /// already known keeps its configuration.
    /// </summary>
    /// <returns>The names added.</returns>
    public static IReadOnlyList<string> AddFrom(Assembly assembly)
    {
        List<string> added = [];
        lock (Gate)
        {
            foreach ((string name, (Assembly Assembly, string Resource) entry) in LoadResourceNames(assembly))
            {
                if (Resources.TryAdd(name, entry)) added.Add(name);
            }
        }
        return added;
    }

    private static string TrimExtension(string name) =>
        name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length] : name;

    private static Dictionary<string, (Assembly, string)> LoadResourceNames(Assembly assembly)
    {
        Dictionary<string, (Assembly, string)> resources = [with(StringComparer.OrdinalIgnoreCase)];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                resourceName.EndsWith(Extension, StringComparison.Ordinal))
            {
                resources[resourceName[ResourcePrefix.Length..^Extension.Length]] = (assembly, resourceName);
            }
        }

        return resources;
    }

    private static string[] GetSortedNames()
    {
        string[] names = [.. Resources.Keys];
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
