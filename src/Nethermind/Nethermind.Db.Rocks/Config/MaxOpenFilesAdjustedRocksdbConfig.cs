// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Rocks.Config;

public class MaxOpenFilesAdjustedRocksdbConfig(
    IRocksDbConfig baseConfig,
    int maxOpenFiles
) : IRocksDbConfig
{
    public ulong? WriteBufferSize => baseConfig.WriteBufferSize;

    public ulong? WriteBufferNumber => baseConfig.WriteBufferNumber;

    public string RocksDbOptions => baseConfig.RocksDbOptions;

    public string AdditionalRocksDbOptions => baseConfig.AdditionalRocksDbOptions;

    public int? MaxOpenFiles => maxOpenFiles;

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
