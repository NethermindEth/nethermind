// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks.Config;

public class RocksDbConfigFactory(IDbConfig dbConfig, IPruningConfig pruningConfig, IHardwareInfo hardwareInfo, ILogManager logManager, bool validateConfig = true) : IRocksDbConfigFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<IRocksDbConfigFactory>();
    private bool _maxOpenFilesInitialized;

    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        // Automatically adjust MaxOpenFiles if not configured (only once)
        if (!_maxOpenFilesInitialized)
        {
            _maxOpenFilesInitialized = true;

            if (dbConfig.MaxOpenFiles is null && hardwareInfo.MaxOpenFilesLimit.HasValue)
            {
                int systemLimit = hardwareInfo.MaxOpenFilesLimit.Value;

                // Only cap MaxOpenFiles on systems with genuinely low file descriptor limits
                // (e.g. macOS default 256, restricted Docker 1024). Setting any finite value
                // forces RocksDB to use an LRU table cache instead of keeping all SST handles
                // open, which adds measurable overhead on every read. On systems with high
                // limits (typical Linux servers: 1048576), leave unlimited for best performance.
                const int LowLimitThreshold = 10000;
                if (systemLimit < LowLimitThreshold)
                {
                    // Apply 80% of system limit per database. On low-limit systems the total
                    // across ~15 databases may exceed the OS limit, but RocksDB will gracefully
                    // handle open() failures via its table cache eviction. The per-DB value
                    // stays high so databases with many SST files (state DB) can still cache
                    // most of their file handles.
                    int perDbLimit = Math.Max(128, (int)(systemLimit * 0.8));

                    if (_logger.IsInfo)
                    {
                        _logger.Info($"Low system open files limit ({systemLimit}). Setting MaxOpenFiles to {perDbLimit} per database to prevent FD exhaustion.");
                    }

                    dbConfig.MaxOpenFiles = perDbLimit;
                }
                else if (_logger.IsDebug)
                {
                    _logger.Debug($"System open files limit is {systemLimit}. Leaving MaxOpenFiles unlimited for best performance.");
                }
            }

            bool skipSstChecks = dbConfig.SkipCheckingSstFileSizesOnDbOpen ?? RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            if (skipSstChecks)
            {
                if (_logger.IsTrace) _logger.Trace("Skipping SST file size checks on DB open for faster startup.");
                dbConfig.RocksDbOptions += "skip_checking_sst_file_sizes_on_db_open=true;";
            }
        }

        IRocksDbConfig rocksDbConfig = new PerTableDbConfig(dbConfig, databaseName, columnName, validateConfig);
        if (databaseName.StartsWith("State"))
        {
            if (!pruningConfig.Mode.IsMemory())
            {
                if (_logger.IsInfo) _logger.Info($"Using archive mode State Db config.");

                ulong writeBufferSize = rocksDbConfig.WriteBufferSize ?? 0;
                if (writeBufferSize < dbConfig.StateDbArchiveModeWriteBufferSize) writeBufferSize = dbConfig.StateDbArchiveModeWriteBufferSize;

                rocksDbConfig = new AdjustedRocksdbConfig(
                    rocksDbConfig,
                    dbConfig.StateDbArchiveModeRocksDbOptions!,
                    writeBufferSize
                );
            }
            else if (hardwareInfo.AvailableMemoryBytes >= IHardwareInfo.StateDbLargerMemoryThreshold)
            {
                if (_logger.IsInfo) _logger.Info($"Detected {hardwareInfo.AvailableMemoryBytes / 1.GiB} GB of available memory. Applying large memory State Db config.");

                ulong writeBufferSize = rocksDbConfig.WriteBufferSize ?? 0;
                if (writeBufferSize < dbConfig.StateDbLargeMemoryWriteBufferSize) writeBufferSize = dbConfig.StateDbLargeMemoryWriteBufferSize;

                rocksDbConfig = new AdjustedRocksdbConfig(
                    rocksDbConfig,
                    dbConfig.StateDbLargeMemoryRocksDbOptions!,
                    writeBufferSize
                );
            }

            if (pruningConfig.Mode.IsMemory())
            {
                ulong totalWriteBufferMb = rocksDbConfig.WriteBufferNumber!.Value * rocksDbConfig.WriteBufferSize!.Value / (ulong)1.MB;
                double minimumWriteBufferMb = 0.2 * pruningConfig.DirtyCacheMb;
                if (totalWriteBufferMb < minimumWriteBufferMb)
                {
                    ulong minimumWriteBufferSize = (ulong)Math.Ceiling((minimumWriteBufferMb * 1.MB) / rocksDbConfig.WriteBufferNumber!.Value);

                    if (_logger.IsInfo) _logger.Info($"Adjust state DB write buffer size to {minimumWriteBufferSize / (ulong)1.MB} MB to account for pruning cache.");

                    rocksDbConfig = new AdjustedRocksdbConfig(
                        rocksDbConfig,
                        "",
                        minimumWriteBufferSize
                    );
                }
            }

        }

        return rocksDbConfig;
    }
}
