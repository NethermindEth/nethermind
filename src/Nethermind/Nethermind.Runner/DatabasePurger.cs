// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Runner;

internal static class DatabasePurger
{
    private static readonly HashSet<string> NetworkDbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        DbNames.PeersDb,
        DbNames.DiscoveryNodes,
        DbNames.DiscoveryV5Nodes
    };

    /// <summary>
    /// Deletes database files from <paramref name="basePath"/>.
    /// When <paramref name="preserveNetwork"/> is true, peer and discovery directories are kept.
    /// </summary>
    public static void Purge(string basePath, bool preserveNetwork, ILogger logger)
    {
        string fullPath = Path.GetFullPath(basePath);

        // Safety guard: refuse to delete filesystem roots
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            logger.Error($"Refusing to delete path that looks like a filesystem root: {fullPath}");
            return;
        }

        if (!Directory.Exists(fullPath))
            return;

        string action = preserveNetwork ? "Force resync" : "Purge";

        try
        {
            if (preserveNetwork)
            {
                foreach (string dir in Directory.EnumerateDirectories(fullPath))
                {
                    if (!NetworkDbNames.Contains(Path.GetFileName(dir)))
                    {
                        if (logger.IsInfo) logger.Info($"{action}: deleting {dir}");
                        Directory.Delete(dir, recursive: true);
                    }
                }

                foreach (string file in Directory.EnumerateFiles(fullPath))
                {
                    if (logger.IsInfo) logger.Info($"{action}: deleting {file}");
                    File.Delete(file);
                }
            }
            else
            {
                if (logger.IsInfo) logger.Info($"{action}: deleting {fullPath}");
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.Error($"{action} failed on {fullPath}", ex);
            throw new InvalidOperationException($"Database {action.ToLowerInvariant()} failed. Some files may be locked or read-only.", ex);
        }
    }
}
