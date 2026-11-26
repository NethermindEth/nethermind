// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;
using Nethermind.Db.Rocks.Config;

namespace Nethermind.JsonRpc.TraceStore;

internal class TraceStoreRocksDbConfigFactory(IRocksDbConfigFactory inner, ITraceStoreConfig config) : IRocksDbConfigFactory
{
    public IRocksDbConfig GetForDatabase(string? databaseName, string? columnName)
    {
        if (databaseName != TraceStorePlugin.DbName)
            return inner.GetForDatabase(databaseName, columnName);

        ValidateOptions(config.RocksDbOptions, nameof(ITraceStoreConfig.RocksDbOptions));
        ValidateOptions(config.AdditionalRocksDbOptions, nameof(ITraceStoreConfig.AdditionalRocksDbOptions));
        return new TraceStoreRocksDbConfig(inner.GetForDatabase(null, null), config);
    }

    private static void ValidateOptions(string options, string propertyName)
    {
        if (options.Length != 0 && !options.EndsWith(';'))
            throw new InvalidConfigurationException($"TraceStore.{propertyName} must end with a semicolon.", -1);
    }

    private sealed class TraceStoreRocksDbConfig(IRocksDbConfig inner, ITraceStoreConfig config) : IRocksDbConfig
    {
        public string RocksDbOptions => inner.RocksDbOptions + config.RocksDbOptions;
        public string AdditionalRocksDbOptions => inner.AdditionalRocksDbOptions + config.AdditionalRocksDbOptions;
        public ulong? WriteBufferSize => inner.WriteBufferSize;
        public ulong? WriteBufferNumber => inner.WriteBufferNumber;
        public int? MaxOpenFiles => inner.MaxOpenFiles;
        public bool WriteAheadLogSync => inner.WriteAheadLogSync;
        public ulong? ReadAheadSize => inner.ReadAheadSize;
        public bool EnableDbStatistics => inner.EnableDbStatistics;
        public uint StatsDumpPeriodSec => inner.StatsDumpPeriodSec;
        public bool? VerifyChecksum => inner.VerifyChecksum;
        public ulong? RowCacheSize => inner.RowCacheSize;
        public bool EnableFileWarmer => inner.EnableFileWarmer;
        public double CompressibilityHint => inner.CompressibilityHint;
        public FlushOnExitMode FlushOnExit => inner.FlushOnExit;
        public nint? BlockCache => inner.BlockCache;
        public bool CompactOnDeletions => inner.CompactOnDeletions;
    }
}
