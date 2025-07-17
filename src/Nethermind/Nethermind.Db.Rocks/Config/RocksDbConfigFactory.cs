// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks.Config;

public class RocksDbConfigFactory(IDbConfig dbConfig, IHardwareInfo hardwareInfo, ILogManager logManager) : IRocksDbConfigFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<IRocksDbConfigFactory>();
    private readonly long StateDbLargerMemoryThreshold = 32.GiB();

    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        IRocksDbConfig rocksDbConfig = new PerTableDbConfig(dbConfig, databaseName, columnName);
        if (databaseName.StartsWith("State") && hardwareInfo.AvailableMemoryBytes > StateDbLargerMemoryThreshold)
        {
            if (_logger.IsDebug) _logger.Debug($"Detected {hardwareInfo.AvailableMemoryBytes / 1.GiB()} GB of available memory. Applying large memory State Db config.");

            ulong writeBufferSize = rocksDbConfig.WriteBufferSize ?? 0;
            if (writeBufferSize < dbConfig.StateDbLargeMemoryWriteBufferSize)
            {
                writeBufferSize = dbConfig.StateDbLargeMemoryWriteBufferSize;
            }

            rocksDbConfig = new MemoryAdjustedRocksdbConfig(
                rocksDbConfig,
                dbConfig.StateDbLargeMemoryAdditionalRocksDbOptions!,
                writeBufferSize
            );
        }

        return rocksDbConfig;
    }
}
