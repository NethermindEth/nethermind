// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Config;

namespace Nethermind.Runner;

internal static class ConfigFileLocator
{
    /// <summary>
    /// Finds the configuration file for a <c>--config</c> value, or returns <c>null</c> when only a built-in configuration can match it.
    /// </summary>
    /// <remarks>
    /// A bare name is looked up in <paramref name="configsDir"/> (also with <c>.json</c> appended) before the current directory,
    /// because a directory given explicitly is the one the user wants searched.
    /// </remarks>
    public static string? Find(string config, string? configsDir)
    {
        if (configsDir is not null && string.IsNullOrEmpty(Path.GetDirectoryName(config)))
        {
            string path = Path.GetFullPath(Path.Join(configsDir, config));

            if (File.Exists(path))
                return path;

            if (File.Exists($"{path}.json"))
                return $"{path}.json";
        }

        return File.Exists(config) ? Path.GetFullPath(config) : null;
    }

    public static string NotFoundMessage(string config)
    {
        string name = Path.GetFileNameWithoutExtension(config);

        // Removed forms of a built-in name, such as configs/mainnet.json or mainnet.cfg.
        string? builtInName = BuiltInConfigs.Names.FirstOrDefault(
            candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        string hint = builtInName is null ? string.Empty : $" Did you mean '--config {builtInName}'?";

        return $"Configuration file not found. It is neither a file nor a built-in configuration.{hint} Built-in configurations: {string.Join(", ", BuiltInConfigs.Names)}";
    }
}
