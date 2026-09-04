// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public static class FlatDbConfigExtensions
{
    /// <summary>Whether flat history is bounded by a floor. Decides the on-disk row format (and through it the
    /// slice validation), the RocksDB deletion tuning and the capture-off refusal. Starting the rolling pruner is a
    /// narrower question answered in <c>HistoryWindowPruner.Start</c>. An extension rather than a member so it
    /// stays out of the options reference and cannot be stubbed away from the mode it reads.</summary>
    public static bool IsHistoryWindowed(this IFlatDbConfig config) => config.HistoryRetention != HistoryRetentionMode.None;
}
