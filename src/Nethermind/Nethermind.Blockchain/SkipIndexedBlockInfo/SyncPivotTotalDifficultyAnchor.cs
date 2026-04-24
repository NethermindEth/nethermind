// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Blockchain.SkipIndexedBlockInfo;

/// <summary>
/// Resolves the TD anchor from the static <see cref="ISyncConfig"/> pivot values (no IBlockTree
/// dependency, avoiding the BlockTree -> SkipIndexedBlockInfoStore -> anchor -> BlockTree DI
/// cycle). Live pivot movement (via <see cref="IBlockTree.SyncPivot"/>) is handled by other
/// callers that can see the updated value and would set <see cref="ISyncConfig.PivotNumber"/>.
/// </summary>
public sealed class SyncPivotTotalDifficultyAnchor(
    ISyncConfig syncConfig) : ITotalDifficultyAnchor
{
    public TotalDifficultyAnchor? TryGet()
    {
        long pivotNumber = syncConfig.PivotNumber;
        if (pivotNumber <= 0) return null;
        string? pivotHashStr = syncConfig.PivotHash;
        if (string.IsNullOrEmpty(pivotHashStr)) return null;
        Hash256 pivotHash = new(pivotHashStr);
        if (pivotHash == Keccak.Zero) return null;

        // PivotTotalDifficulty must be supplied explicitly. Falling back to TerminalTotalDifficulty
        // would overclaim on pre-merge chains whose TD is far below TTD — the store would then
        // short-circuit walks and report TTD for every ancestor of the pivot, breaking
        // ImprovementRequirementSatisfied and similar TD-based checks.
        UInt256 configTd = syncConfig.PivotTotalDifficultyParsed;
        if (configTd == UInt256.Zero) return null;

        return new TotalDifficultyAnchor(pivotNumber, pivotHash.ValueHash256, configTd);
    }
}
