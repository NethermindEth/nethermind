// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History;

/// <summary>
/// Gate consulted by <see cref="HistoryWindowPruner"/> before every pass: pruning must never run concurrently
/// with a concurrent backfill importer walking the same columns. The default (no backfill feature installed)
/// implementation never blocks the pruner; the concurrent backfill importer replaces this binding with one
/// reflecting its own running state.
/// </summary>
public interface IBackfillInterlock
{
    bool IsBackfillActive { get; }
}

public sealed class NullBackfillInterlock : IBackfillInterlock
{
    public static readonly NullBackfillInterlock Instance = new();

    private NullBackfillInterlock() { }

    public bool IsBackfillActive => false;
}
