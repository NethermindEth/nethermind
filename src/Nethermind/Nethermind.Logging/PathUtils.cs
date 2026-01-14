// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;

namespace Nethermind.Logging;

public static class PathUtils
{
    static PathUtils()
    {
        string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);

        ExecutingDirectory = processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("ReSharperTestRunner", StringComparison.OrdinalIgnoreCase)
            // A workaround for tests in JetBrains Rider ignoring MTP:
            // https://youtrack.jetbrains.com/projects/RIDER/issues/RIDER-131530
            ? AppContext.BaseDirectory 
            : Path.GetDirectoryName(Environment.ProcessPath);
    }

    public static string ExecutingDirectory { get; }

    public static string GetApplicationResourcePath(this string resourcePath, string overridePrefixPath = null)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            resourcePath = string.Empty;
        }

        if (Path.IsPathRooted(resourcePath) || IsExplicitlyRelative(resourcePath))
        {
            return resourcePath;
        }

        if (string.IsNullOrEmpty(overridePrefixPath))
        {
            return Path.Combine(ExecutingDirectory, resourcePath);
        }

        return Path.IsPathRooted(overridePrefixPath) || IsExplicitlyRelative(overridePrefixPath)
            ? Path.Combine(overridePrefixPath, resourcePath)
            : Path.Combine(ExecutingDirectory, overridePrefixPath, resourcePath);
    }

    static readonly string[] RelativePrefixes = [.. new[]
    {
        $".{Path.DirectorySeparatorChar}",
        $".{Path.AltDirectorySeparatorChar}",
        $"..{Path.DirectorySeparatorChar}",
        $"..{Path.AltDirectorySeparatorChar}",
    }.Distinct()];

    public static bool IsExplicitlyRelative(string resourcePath) => RelativePrefixes.Any(resourcePath.StartsWith);
}
