// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

                rocksDbConfig = new MemoryAdjustedRocksdbConfig(
                    rocksDbConfig,
                    dbConfig.StateDbArchiveModeRocksDbOptions!,
                    writeBufferSize
                );
            }
            else if (hardwareInfo.AvailableMemoryBytes > IHardwareInfo.StateDbLargerMemoryThreshold)
            {
                if (_logger.IsInfo) _logger.Info($"Detected {hardwareInfo.AvailableMemoryBytes / 1.GiB()} GB of available memory. Applying large memory State Db config.");

                ulong writeBufferSize = rocksDbConfig.WriteBufferSize ?? 0;
                if (writeBufferSize < dbConfig.StateDbLargeMemoryWriteBufferSize) writeBufferSize = dbConfig.StateDbLargeMemoryWriteBufferSize;

                rocksDbConfig = new MemoryAdjustedRocksdbConfig(
                    rocksDbConfig,
                    dbConfig.StateDbLargeMemoryRocksDbOptions!,
                    writeBufferSize
                );
            }
        }

        return rocksDbConfig;
    }
}
