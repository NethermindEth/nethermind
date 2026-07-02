// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.StateDiff.Core.Data;

/// <summary>
/// Slim per-block diff produced by <see cref="Diff.TrieDiffWalker.ComputeDiff"/>: code-hash and
/// slot-count changes plus net byte / leaf deltas. Persisted verbatim per block, so kept minimal.
/// </summary>
public readonly record struct TrieDiff(
    IReadOnlyList<SlotCountChange> SlotCountChanges,
    IReadOnlyList<CodeHashChange> CodeHashChanges,
    long AccountTrieBytesDelta = 0,
    long StorageTrieBytesDelta = 0,
    long AccountsAddedDelta = 0)
{
    /// <summary>Zero-delta sentinel returned when both roots are equal.</summary>
    public static TrieDiff Empty { get; } = new(
        SlotCountChanges: Array.Empty<SlotCountChange>(),
        CodeHashChanges: Array.Empty<CodeHashChange>());
}
