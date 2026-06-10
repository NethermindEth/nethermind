// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool.Profiling;

/// <summary>
/// No-op transaction profiler used when profiling is not wired for a component.
/// </summary>
public sealed class NullTxProfilingDb : ITxProfilingDb
{
    /// <summary>
    /// Shared no-op profiler instance.
    /// </summary>
    public static ITxProfilingDb Instance { get; } = new NullTxProfilingDb();

    /// <inheritdoc/>
    public string? FilePath => null;

    /// <inheritdoc/>
    public long DroppedRecords => 0;

    private NullTxProfilingDb() { }

    /// <inheritdoc/>
    public void RecordHash(
        string eventName,
        Hash256? txHash,
        string? peer = null,
        string? protocol = null,
        string? direction = null,
        string? reason = null,
        TxType? txType = null,
        int? txSize = null) { }

    /// <inheritdoc/>
    public void RecordTx(
        string eventName,
        Transaction? tx,
        string? peer = null,
        string? protocol = null,
        string? direction = null,
        string? reason = null,
        AcceptTxResult? result = null) { }

    /// <inheritdoc/>
    public void RecordResource(
        string eventName,
        string resourceId,
        string? peer = null,
        string? reason = null) { }
}
