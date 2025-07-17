// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Rocks.Config;

public class AdjustedRocksdbConfig(
    IRocksDbConfig baseConfig,
    string additionalRocksDbOptions,
    ulong writeBufferSize
) : IRocksDbConfig
{
    public ulong? WriteBufferSize => writeBufferSize;

    public ulong? WriteBufferNumber => baseConfig.WriteBufferNumber;

    // Note: not AdditionalRocksDbOptions so that user can still override option.
    public string RocksDbOptions => baseConfig.RocksDbOptions + additionalRocksDbOptions;

    public string AdditionalRocksDbOptions => baseConfig.AdditionalRocksDbOptions;

    public int? MaxOpenFiles => baseConfig.MaxOpenFiles;

    public bool WriteAheadLogSync => baseConfig.WriteAheadLogSync;

    public ulong? ReadAheadSize => baseConfig.ReadAheadSize;

    public bool EnableDbStatistics => baseConfig.EnableDbStatistics;

    public uint StatsDumpPeriodSec => baseConfig.StatsDumpPeriodSec;

    public bool? VerifyChecksum => baseConfig.VerifyChecksum;

    public ulong? RowCacheSize => baseConfig.RowCacheSize;

    public bool EnableFileWarmer => baseConfig.EnableFileWarmer;

    public double CompressibilityHint => baseConfig.CompressibilityHint;

    public bool FlushOnExit => baseConfig.FlushOnExit;
}
