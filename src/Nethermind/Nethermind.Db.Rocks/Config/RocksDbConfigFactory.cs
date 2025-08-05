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

    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
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
