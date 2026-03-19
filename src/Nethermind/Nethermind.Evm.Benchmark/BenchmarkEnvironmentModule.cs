// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Provides RocksDB management utilities and temp directory lifecycle for benchmarks.
/// </summary>
public sealed class BenchmarkEnvironmentModule
{
    /// <summary>
    /// The base path for RocksDB storage used by the benchmark.
    /// </summary>
    public string BasePath { get; }

    public BenchmarkEnvironmentModule()
    {
        BasePath = Path.Combine(Path.GetTempPath(), $"nethermind-bench-{Guid.NewGuid()}");
        Directory.CreateDirectory(BasePath);
    }

    /// <summary>
    /// Flushes WAL and memtables, then compacts StateDb, CodeDb, and MetadataDb.
    /// </summary>
    public static void FlushAndCompact(IDbProvider dbProvider)
    {
        IDb[] dbs = [dbProvider.StateDb, dbProvider.CodeDb, dbProvider.MetadataDb];

        foreach (IDb db in dbs)
        {
            if (db is DbOnTheRocks rocksDb)
            {
                rocksDb.Flush();
                rocksDb.Compact();
            }
        }
    }

    /// <summary>
    /// Disables auto-compaction on StateDb, CodeDb, and MetadataDb to reduce background I/O during benchmarks.
    /// </summary>
    public static void DisableAutoCompaction(IDbProvider dbProvider)
    {
        TuneDbs(dbProvider, ITunableDb.TuneType.DisableCompaction);
    }

    /// <summary>
    /// Re-enables default compaction behavior on StateDb, CodeDb, and MetadataDb.
    /// </summary>
    public static void EnableAutoCompaction(IDbProvider dbProvider)
    {
        TuneDbs(dbProvider, ITunableDb.TuneType.Default);
    }

    /// <summary>
    /// Deletes the temp directory on a best-effort basis.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(BasePath))
            {
                Directory.Delete(BasePath, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup — ignore failures.
        }
    }

    private static void TuneDbs(IDbProvider dbProvider, ITunableDb.TuneType tuneType)
    {
        IDb[] dbs = [dbProvider.StateDb, dbProvider.CodeDb, dbProvider.MetadataDb];

        foreach (IDb db in dbs)
        {
            if (db is ITunableDb tunableDb)
            {
                tunableDb.Tune(tuneType);
            }
        }
    }
}
