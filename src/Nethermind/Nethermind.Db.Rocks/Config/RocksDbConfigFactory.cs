// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks.Config;

public class RocksDbConfigFactory(IDbConfig dbConfig, IPruningConfig pruningConfig, IHardwareInfo hardwareInfo, ILogManager logManager) : IRocksDbConfigFactory
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
                // Estimate ~15 databases (can vary by configuration, but this is a reasonable estimate)
                // Reserve some file descriptors for the system and other operations (like network sockets)
                // Use a conservative approach: systemLimit / 20 per database
                // This accounts for ~15 databases plus safety margin for non-DB file descriptors
                int perDbLimit = Math.Max(256, systemLimit / 20);

                if (_logger.IsInfo)
                {
                    _logger.Info($"Detected system open files limit of {systemLimit}. Setting MaxOpenFiles to {perDbLimit} per database.");
                }

                dbConfig.MaxOpenFiles = perDbLimit;
            }
        }

        IRocksDbConfig rocksDbConfig = new PerTableDbConfig(dbConfig, databaseName, columnName);
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
                if (_logger.IsInfo) _logger.Info($"Detected {hardwareInfo.AvailableMemoryBytes / 1.GiB()} GB of available memory. Applying large memory State Db config.");

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
                ulong totalWriteBufferMb = rocksDbConfig.WriteBufferNumber!.Value * rocksDbConfig.WriteBufferSize!.Value / (ulong)1.MB();
                double minimumWriteBufferMb = 0.2 * pruningConfig.DirtyCacheMb;
                if (totalWriteBufferMb < minimumWriteBufferMb)
                {
                    ulong minimumWriteBufferSize = (ulong)Math.Ceiling((minimumWriteBufferMb * 1.MB()) / rocksDbConfig.WriteBufferNumber!.Value);

                    if (_logger.IsInfo) _logger.Info($"Adjust state DB write buffer size to {minimumWriteBufferSize / (ulong)1.MB()} MB to account for pruning cache.");

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
