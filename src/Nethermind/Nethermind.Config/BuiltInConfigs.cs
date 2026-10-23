// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Nethermind.Config;

/// <summary>
/// The configuration files shipped as embedded resources of this assembly.
/// </summary>
public static class BuiltInConfigs
{
    private const string ResourcePrefix = "configs/";
    private const string Extension = ".json";

    private static readonly Assembly ResourceAssembly = typeof(BuiltInConfigs).Assembly;
    private static readonly Dictionary<string, string> Resources = LoadResourceNames();

    /// <summary>
    /// The names of the built-in configurations, without the file extension, sorted.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = GetSortedNames();

    /// <summary>
    /// Opens the built-in configuration with the given name. The name is case-insensitive and can end with <c>.json</c>.
    /// </summary>
    public static Stream? Open(string name) =>
        Resources.TryGetValue(TrimExtension(name), out string? resourceName)
            ? ResourceAssembly.GetManifestResourceStream(resourceName)
            : null;

    public static bool Exists(string name) => Resources.ContainsKey(TrimExtension(name));

    private static string TrimExtension(string name) =>
        name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length] : name;

    private static Dictionary<string, string> LoadResourceNames()
    {
        Dictionary<string, string> resources = new(StringComparer.OrdinalIgnoreCase);

        foreach (string resourceName in ResourceAssembly.GetManifestResourceNames())
        {
            if (resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                resourceName.EndsWith(Extension, StringComparison.Ordinal))
            {
                resources[resourceName[ResourcePrefix.Length..^Extension.Length]] = resourceName;
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
