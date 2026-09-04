// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public static class FlatDbConfigExtensions
{
    /// <summary>The one answer every windowed behaviour keys off: the row format, the pruner, the RocksDB tuning
    /// and the slice validation. An extension rather than a member so it stays out of the options reference and
    /// cannot be stubbed away from the retention mode it reads.</summary>
    public static bool IsHistoryWindowed(this IFlatDbConfig config) => config.HistoryRetention != HistoryRetentionMode.None;
}
