// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using DbMetrics = Nethermind.Db.Metrics;
using EvmMetrics = Nethermind.Evm.Metrics;

namespace Nethermind.State;

#pragma warning disable NETH003 // File name does not match the contained type

/// <summary>
/// Folds a world-state scope's <see cref="LocalMetrics"/> accumulator into the global
/// <see cref="DbMetrics"/> / <see cref="EvmMetrics"/> counters and resets it.
/// </summary>
/// <remarks>
/// Lives in <c>Nethermind.State</c> because it is the only assembly that references both
/// <c>Nethermind.Db</c> and <c>Nethermind.Evm</c>. Called on the same thread that accumulated, so
/// the global counters' main/other split (keyed off <c>IsBlockProcessingThread</c>) stays correct.
/// </remarks>
internal static class LocalMetricsFlush
{
    internal static void Flush(this LocalMetrics m)
    {
        if (m.StateTreeCacheHits != 0) DbMetrics.AddStateTreeCacheHits(m.StateTreeCacheHits);
        if (m.StateTreeReads != 0) DbMetrics.AddStateTreeReads(m.StateTreeReads);
        if (m.StateTreeWrites != 0) DbMetrics.IncrementStateTreeWrites(m.StateTreeWrites);
        if (m.StateSkippedWrites != 0) DbMetrics.IncrementStateSkippedWrites(m.StateSkippedWrites);
        if (m.StorageTreeCache != 0) DbMetrics.AddStorageTreeCache(m.StorageTreeCache);
        if (m.StorageTreeReads != 0) DbMetrics.AddStorageTreeReads(m.StorageTreeReads);

        if (m.AccountWrites != 0) EvmMetrics.AddAccountWrites(m.AccountWrites);
        if (m.AccountDeleted != 0) EvmMetrics.AddAccountDeleted(m.AccountDeleted);
        if (m.StorageWrites != 0) EvmMetrics.AddStorageWrites(m.StorageWrites);
        if (m.CodeWrites != 0) EvmMetrics.AddCodeWrites(m.CodeWrites);
        if (m.CodeBytesWritten != 0) EvmMetrics.AddCodeBytesWritten(m.CodeBytesWritten);

        m.Reset();
    }
}
