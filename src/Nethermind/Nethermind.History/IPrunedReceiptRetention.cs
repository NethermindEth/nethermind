// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.History;

/// <summary>
/// Decides whether a block the history pruner is about to delete must keep its receipts and body because something
/// on this node still answers queries for it. Keeps the pruner free of any one backend's retention rules: the default
/// implementation never retains, so a node that configures no such backend pays nothing and carries no coupling.
/// </summary>
public interface IPrunedReceiptRetention
{
    /// <summary>Decides from the header alone, so the caller can ask about a height without reading its body.</summary>
    bool ShouldRetainReceipts(BlockHeader header);

    /// <summary>
    /// Heights in <c>[fromInclusive, toExclusive)</c> whose receipts and body must survive, answered without reading a header.
    /// </summary>
    /// <remarks>
    /// The pruner reclaims by range, so it needs the answer for a span rather than for one height at a time. An
    /// implementation that cannot answer for the whole span narrows <paramref name="answeredFrom"/> and
    /// <paramref name="answeredTo"/> to the part it can; outside that the caller reads headers and asks
    /// <see cref="ShouldRetainReceipts"/>, which costs a header per height but still reclaims the rest by range.
    /// </remarks>
    IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo);

    /// <summary>Heights below this may have stopped qualifying for retention as the chain advanced, so a caller
    /// holding previously retained data below it should re-ask. Zero - the default - promises no retention ever
    /// expires, so nothing already retained needs revisiting.</summary>
    ulong ExpiredRetentionUpperBound() => 0;

    /// <summary>Called at the start of every pruning pass, after the pruner has loaded its pointers: the oldest
    /// height whose receipts this node actually holds, and how far each of the two reclaiming cursors - the main
    /// reclaim and the expired-slice cleanup - has provably reached. Lets an implementation record from which
    /// height its retention has been in force: anything reclaimed before its first call predates it, and either
    /// cursor having run past an entry's recorded reach while it was unconfigured lapses it. The default ignores
    /// it.</summary>
    void OnPruningPassStarting(ulong oldestStoredReceipts, ulong reclaimedThrough, ulong sliceCleanupThrough) { }

    /// <summary>Called on every save of the pruner's pointers - including saves that persist nothing - with the
    /// heights the two reclaims have actually reached, extending the proof exactly that far: per persisted chunk,
    /// never per intended pass, so an ungraceful stop cannot leave the cursors ahead of the proof. The default
    /// ignores it.</summary>
    void OnPruningProgress(ulong reclaimedThrough, ulong sliceCleanupThrough) { }
}
