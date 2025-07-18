// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Rocks.Config;

public interface IRocksDbConfig
{
    ulong? WriteBufferSize { get; }
    ulong? WriteBufferNumber { get; }
    string RocksDbOptions { get; }
    string AdditionalRocksDbOptions { get; }
    int? MaxOpenFiles { get; }
    bool WriteAheadLogSync { get; }
    ulong? ReadAheadSize { get; }
    bool EnableDbStatistics { get; }
    uint StatsDumpPeriodSec { get; }
    bool? VerifyChecksum { get; }
    ulong? RowCacheSize { get; }
    bool EnableFileWarmer { get; }
    double CompressibilityHint { get; }
    bool FlushOnExit { get; }
}
