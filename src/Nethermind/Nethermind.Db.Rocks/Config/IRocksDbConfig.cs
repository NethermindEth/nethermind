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
    FlushOnExitMode FlushOnExit { get; }
    nint? BlockCache { get; }

    /// <summary>Marks SST files whose recent keys are tombstone-heavy for compaction as they are written, so a
    /// store that mass-deletes gives its space back without an external trigger - deletions shrink levels, and
    /// shrinking levels never reach the size targets that normally schedule compaction. Off by default: a store
    /// that never deletes pays nothing either way, and one that does must opt in deliberately.</summary>
    bool CompactOnDeletions => false;
}
