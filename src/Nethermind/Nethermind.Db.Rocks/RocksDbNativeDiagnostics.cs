// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Logs the loaded RocksDB native library and allocator once at first DB open. Both the RocksDbSharp
/// probe and <c>LD_PRELOAD</c> fall back silently, so the effective allocator is otherwise invisible.
/// </summary>
internal static class RocksDbNativeDiagnostics
{
    private static int _logged;

    internal static void LogLoadedNative(ILogger logger)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }

        try
        {
            if (logger.IsInfo) logger.Info($"RocksDB native library loaded: {NativeImport.Auto.LoadedPath ?? "<unknown>"}");

            if (OperatingSystem.IsLinux())
            {
                HashSet<string> mapped = [];
                foreach (string line in File.ReadLines("/proc/self/maps"))
                {
                    int slash = line.LastIndexOf('/');
                    if (slash < 0)
                    {
                        continue;
                    }

                    string name = line[(slash + 1)..];
                    if (name.Contains("rocksdb", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("malloc", StringComparison.OrdinalIgnoreCase))
                    {
                        mapped.Add(name);
                    }
                }

                if (logger.IsInfo)
                    logger.Info($"Allocator-relevant native maps: [{string.Join(", ", mapped)}]; " +
                                $"LD_PRELOAD={Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "<none>"}");
            }
        }
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"RocksDB native diagnostics failed: {e.Message}");
        }
    }
}
