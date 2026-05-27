// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.StateDiff.Core.Data;

/// <summary>
/// Slim two-list diff produced by <see cref="Diff.TrieDiffWalker.ComputeDiff"/>:
/// the per-block <see cref="CodeHashChanges"/> and <see cref="SlotCountChanges"/>
/// streams the v19 sidecar needs to maintain its global trackers, plus a small
/// trio of net byte / leaf deltas (<see cref="AccountTrieBytesDelta"/>,
/// <see cref="StorageTrieBytesDelta"/>, <see cref="AccountsAddedDelta"/>) that
/// the sidecar lifts directly into Prometheus.
/// <para>
/// Heavier counters (per-node-type breakdown, depth distribution, gross
/// added/removed splits) still live in the legacy
/// <c>Nethermind.StateComposition</c> walker and are intentionally omitted here:
/// the writer plugin persists this diff verbatim into the <c>BlockDiffs</c> column
/// family, where every additional field amplifies write volume across millions of
/// blocks.
/// </para>
/// </summary>
public readonly record struct TrieDiff(
    IReadOnlyList<SlotCountChange> SlotCountChanges,
    IReadOnlyList<CodeHashChange> CodeHashChanges,
    long AccountTrieBytesDelta = 0,
    long StorageTrieBytesDelta = 0,
    long AccountsAddedDelta = 0)
{
    /// <summary>
    /// Zero-delta sentinel returned when both roots are equal. Callers iterate
    /// the empty lists without allocating temporary collections.
    /// </summary>
    public static TrieDiff Empty { get; } = new(
        SlotCountChanges: Array.Empty<SlotCountChange>(),
        CodeHashChanges: Array.Empty<CodeHashChange>());
}
