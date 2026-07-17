// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Logs which RocksDB native library was actually loaded and which allocator libraries are mapped
/// into the process. The RocksDbSharp loader probes librocksdb-jemalloc/librocksdb/librocksdb-musl
/// and silently falls back, so the effective allocator is environment-dependent and otherwise invisible.
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
            string loadedPath = NativeImport.Auto.LoadedPath ?? "<unknown>";
            if (logger.IsInfo) logger.Info($"RocksDB native library loaded: {loadedPath}");

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
                        || name.Contains("jemalloc", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("mimalloc", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("tcmalloc", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("snmalloc", StringComparison.OrdinalIgnoreCase))
                    {
                        mapped.Add(name);
                    }
                }

                string preload = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? "<none>";
                if (logger.IsInfo) logger.Info($"Allocator-relevant native maps: [{string.Join(", ", mapped)}]; LD_PRELOAD={preload}");
            }
        }
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"RocksDB native diagnostics failed: {e.Message}");
        }
    }
}
