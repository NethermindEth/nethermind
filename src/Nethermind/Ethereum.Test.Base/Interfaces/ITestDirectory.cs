// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base;

/// <summary>
/// Marker interface for test directory name prefixes (e.g., "st", "bc", "vm").
/// Used as a generic type parameter in test fixture base classes.
/// </summary>
public interface ITestDirectoryPrefix
{
    static abstract string Value { get; }
}

public readonly struct StPrefix : ITestDirectoryPrefix { public static string Value => "st"; }
public readonly struct BcPrefix : ITestDirectoryPrefix { public static string Value => "bc"; }
public readonly struct VmPrefix : ITestDirectoryPrefix { public static string Value => "vm"; }

/// <summary>
/// Derives test directory names from type metadata by convention.
/// Convention: prefix + class name, with _ → - for hyphens and leading _ stripped for digit-start.
/// </summary>
public static class TestDirectoryHelper
{
    public static string GetDirectoryByPrefix<TSelf>(string prefix)
    {
        string name = typeof(TSelf).Name;
        name = name.Replace('_', '-');
        if (name.StartsWith('-')) name = name[1..];
        return prefix + name;
    }

    /// <summary>
    /// Derives directory from class name by stripping a known suffix and lowercasing.
    /// Used by Pyspec fixtures where the convention is clean.
    /// </summary>
    public static string GetDirectoryByConvention<TSelf>(string suffix)
    {
        string name = typeof(TSelf).Name;
        if (name.EndsWith(suffix, System.StringComparison.Ordinal))
            name = name[..^suffix.Length];

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Derives a JSON filename by stripping a suffix and lowercasing the first character.
    /// Convention: "DifficultyByzantiumTests" → "difficultyByzantium.json"
    /// </summary>
    public static string GetJsonFileByConvention<TSelf>(string suffix, string extension = ".json")
    {
        string name = typeof(TSelf).Name;
        if (name.EndsWith(suffix, System.StringComparison.Ordinal))
            name = name[..^suffix.Length];

        return char.ToLowerInvariant(name[0]) + name[1..] + extension;
    }

    /// <summary>
    /// Reverse of <see cref="GetDirectoryByPrefix{TSelf}"/>: derives expected class name from directory.
    /// Convention: strip prefix, replace - with _, prepend _ if starts with digit.
    /// </summary>
    public static string GetClassNameFromDirectory(string directory, int prefixLength)
    {
        string name = directory[prefixLength..];
        name = name.Replace('-', '_');
        if (name.Length > 0 && char.IsDigit(name[0]))
            name = "_" + name;
        return name;
    }
}
