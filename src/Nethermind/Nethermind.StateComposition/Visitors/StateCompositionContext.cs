// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.StateComposition.Visitors;

/// <summary>
/// Custom INodeContext that combines OldStyleTrieVisitContext fields (Level, IsStorage,
/// BranchChildIndex) with TreePath tracking for account hash reconstruction.
///
/// At VisitAccount time, Path.Path contains the keccak256(address) — the account hash
/// that Geth calls "Owner" in its inspect-trie TopN rankings.
///
/// Storage trie Level resets to 0 (relative depth) to match Geth's per-contract Levels[16].
///
/// Counters is a cached reference to the per-worker VisitorCounters so visit methods avoid
/// a ThreadLocal&lt;T&gt;.Value lookup on every node. It starts null at the trie root (framework
/// creates the root context as default(T)) and is populated on the first visit call by the
/// visitor; subsequent child contexts inherit it via Add/AddStorage.
/// </summary>
public readonly struct StateCompositionContext(TreePath path, int level, bool isStorage, int? branchChildIndex, VisitorCounters? counters = null)
    : INodeContext<StateCompositionContext>
{
    public readonly TreePath Path = path;
    public readonly int Level = level;
    public readonly bool IsStorage = isStorage;
    public readonly int? BranchChildIndex = branchChildIndex;

    /// <summary>
    /// Per-worker counter cache. Null only for the very first (root) context produced by
    /// the framework via <c>default(StateCompositionContext)</c>. The visitor falls back to
    /// <c>_localCounters.Value</c> when null, then propagates the resolved instance into
    /// child contexts so subsequent nodes pay no ThreadLocal cost.
    /// </summary>
    public readonly VisitorCounters? Counters = counters;

    public StateCompositionContext Add(ReadOnlySpan<byte> nibblePath)
    {
        return new StateCompositionContext(Path.Append(nibblePath), Level + 1, IsStorage, null, Counters);
    }

    public StateCompositionContext Add(byte nibble)
    {
        return new StateCompositionContext(Path.Append(nibble), Level + 1, IsStorage, nibble, Counters);
    }

    public StateCompositionContext AddStorage(in ValueHash256 storage)
    {
        // Reset path and level for storage trie traversal.
        // Level resets to 0 so per-contract Levels[16] indexes from depth 0 (matching Geth).
        // Path resets to Empty since storage trie has its own independent path space.
        // Counters is preserved — same worker, same ThreadLocal slot.
        return new StateCompositionContext(TreePath.Empty, 0, true, null, Counters);
    }
}
