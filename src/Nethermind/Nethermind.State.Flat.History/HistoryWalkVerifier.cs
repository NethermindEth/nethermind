// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Proves an unwindowed (v2) archive's content against this node's own headers by rebuilding the state root from
/// rows at EVERY block of a range - per-block because a change attributed to the wrong block leaves the tip root
/// correct while every as-of answer in between is wrong. v2 only (v3 rows are pre-values behind a floor). Rows
/// are key-major, so each subtree's history is one contiguous range: the walk replays subtrees one at a time
/// with at most <c>maxRowsPerPartition</c> rows in memory, splitting a subtree that does not fit and streaming
/// a single key that does not fit, then combines subtree roots upward through per-block series until the trie
/// root is compared to the header. Memory follows the configured budget, never the state size.
/// </summary>
public sealed class HistoryWalkVerifier
{
    public const long DefaultMaxRowsPerPartition = WalkResources.DefaultRowsPerPartition;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IHistoryHeaderSource _headers;
    private readonly HistoryRowFormat _rowFormat;
    private readonly bool _rlpWrapSlots;
    private readonly ILogManager _logManager;
    private readonly long _maxRowsPerPartition;
    private readonly ICommitmentEmitterSource? _emitterSource;

    public HistoryWalkVerifier(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryRowFormat rowFormat,
        ILogManager logManager,
        long maxRowsPerPartition,
        ICommitmentEmitterSource? emitterSource)
        : this(
            history,
            headers,
            rowFormat,
            BasePersistence.ResolveSlotEncoding(db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<HistoryWalkVerifier>()),
            logManager,
            maxRowsPerPartition,
            emitterSource)
    {
    }

    public HistoryWalkVerifier(
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryRowFormat rowFormat,
        bool rlpWrapSlots,
        ILogManager logManager,
        long maxRowsPerPartition,
        ICommitmentEmitterSource? emitterSource)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(logManager);

        RequireUnwindowed(rowFormat);

        _history = history;
        _headers = headers;
        _rowFormat = rowFormat;
        _rlpWrapSlots = rlpWrapSlots;
        _logManager = logManager;
        _maxRowsPerPartition = maxRowsPerPartition > 0 ? maxRowsPerPartition : DefaultMaxRowsPerPartition;
        _emitterSource = emitterSource;
    }

    public static void RequireUnwindowed(HistoryRowFormat rowFormat)
    {
        if (!rowFormat.IsV3) return;

        throw new InvalidConfigurationException(
            "The every-block walk verifier only supports an unwindowed (v2) history: v2 rows are post-values a " +
            "forward walk can apply directly, while a windowed database stores pre-values, carries no rows at " +
            "all for unchanged keys, and has pruned the ancestry a genesis-anchored walk needs.", -1);
    }

    public HistoryWalkVerdict VerifyRange(ulong fromInclusive, ulong toInclusive, CancellationToken token) =>
        VerifyRangeParallel(fromInclusive, toInclusive, 1, token);

    public HistoryWalkVerdict VerifyRangeParallel(ulong fromInclusive, ulong toInclusive, int workers, CancellationToken token)
    {
        if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
        if (fromInclusive > toInclusive)
            throw new ArgumentException($"Range start {fromInclusive} is above its end {toInclusive}.", nameof(fromInclusive));

        ulong granularity = _emitterSource?.WindowGranularity ?? 1;
        if (granularity > 1 && fromInclusive % granularity != 0)
        {
            throw new InvalidConfigurationException(
                $"A commitment build must start at a checkpoint window boundary; block {fromInclusive} is not a " +
                $"multiple of {granularity}.", -1);
        }

        HistoryWalkRun run = new(_history, _headers, _rowFormat, _rlpWrapSlots, _logManager, _maxRowsPerPartition, _emitterSource, fromInclusive, toInclusive, token);
        return run.Execute(workers);
    }
}
