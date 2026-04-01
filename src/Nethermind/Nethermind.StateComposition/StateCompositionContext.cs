// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Custom INodeContext that combines OldStyleTrieVisitContext fields (Level, IsStorage,
/// BranchChildIndex) with TreePath tracking for account hash reconstruction.
///
/// At VisitAccount time, Path.Path contains the keccak256(address) — the account hash
/// that Geth calls "Owner" in its inspect-trie TopN rankings.
///
/// Storage trie Level resets to 0 (relative depth) to match Geth's per-contract Levels[16].
/// </summary>
public readonly struct StateCompositionContext(TreePath path, int level, bool isStorage, int? branchChildIndex)
    : INodeContext<StateCompositionContext>
{
    public readonly TreePath Path = path;
    public readonly int Level = level;
    public readonly bool IsStorage = isStorage;
    public readonly int? BranchChildIndex = branchChildIndex;

    public StateCompositionContext Add(ReadOnlySpan<byte> nibblePath)
    {
        return new StateCompositionContext(Path.Append(nibblePath), Level + 1, IsStorage, null);
    }

    public StateCompositionContext Add(byte nibble)
    {
        return new StateCompositionContext(Path.Append(nibble), Level + 1, IsStorage, nibble);
    }

    public StateCompositionContext AddStorage(in ValueHash256 storage)
    {
        // Reset path and level for storage trie traversal.
        // Level resets to 0 so per-contract Levels[16] indexes from depth 0 (matching Geth).
        // Path resets to Empty since storage trie has its own independent path space.
        return new StateCompositionContext(TreePath.Empty, 0, true, null);
    }
}
