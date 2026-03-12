// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;

namespace Ethereum.Test.Base;

/// <summary>
/// Specifies the test fixture directory name for Ethereum Foundation test loading.
/// Only needed when the directory name cannot be derived by convention.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TestDirectoryAttribute(string directory) : Attribute
{
    public string Directory { get; } = directory;
}

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
/// Derives test directory names from type metadata.
/// </summary>
public static class TestDirectoryHelper
{
    /// <summary>
    /// Derives directory by prepending a prefix to the class name.
    /// Underscores in the class name are converted to hyphens (for directory names containing hyphens).
    /// A leading underscore is stripped (to escape class names that would start with a digit).
    /// Falls back to <see cref="TestDirectoryAttribute"/> if present.
    /// </summary>
    public static string GetDirectoryByPrefix<TSelf>(string prefix)
    {
        TestDirectoryAttribute? attr = typeof(TSelf).GetCustomAttribute<TestDirectoryAttribute>();
        if (attr is not null) return attr.Directory;

        string name = typeof(TSelf).Name;
        name = name.Replace('_', '-');
        if (name.StartsWith('-')) name = name[1..];
        return prefix + name;
    }

    /// <summary>
    /// Derives directory from class name by stripping a known suffix and lowercasing.
    /// Used by Pyspec fixtures where the convention is clean.
    /// Falls back to <see cref="TestDirectoryAttribute"/> if present.
    /// </summary>
    public static string GetDirectoryByConvention<TSelf>(string suffix)
    {
        TestDirectoryAttribute? attr = typeof(TSelf).GetCustomAttribute<TestDirectoryAttribute>();
        if (attr is not null) return attr.Directory;

        string name = typeof(TSelf).Name;
        if (name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];

        return name.ToLowerInvariant();
    }
}
