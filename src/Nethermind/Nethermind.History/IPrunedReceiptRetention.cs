// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.History;

/// <summary>
/// Decides whether a block the history pruner is about to delete must keep its receipts because something on this
/// node still answers queries for it. Keeps the pruner free of any one backend's retention rules: the default
/// implementation never retains, so a node that configures no such backend pays nothing and carries no coupling.
/// </summary>
public interface IPrunedReceiptRetention
{
    bool ShouldRetainReceipts(Block block);

    /// <summary>
    /// Heights in <c>[fromInclusive, toExclusive)</c> whose receipts must survive, answered without reading a block.
    /// </summary>
    /// <remarks>
    /// The pruner reclaims by range, so it needs the answer for a span rather than for one block at a time - reading
    /// every header to ask would put back the per-block cost the range reclaim exists to remove. An implementation
    /// that cannot answer for the whole span narrows <paramref name="answeredFrom"/> and <paramref name="answeredTo"/>
    /// to the part it can; the caller falls back to <see cref="ShouldRetainReceipts"/> block by block outside that,
    /// which is slow but is the behaviour that was there before.
    /// </remarks>
    IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo);
}
