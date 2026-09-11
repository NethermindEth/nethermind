// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

/// <summary>
/// Fake <see cref="IFinalizedStateProvider"/> that simulate previous behaviour where it just check the
/// LatestCommittedBlockNumber minute depth. Not for prod use.
/// TrieStore must be set later.
/// </summary>
/// <param name="depth"></param>
public class TestFinalizedStateProvider(ulong depth) : IFinalizedStateProvider
{
    public TrieStore TrieStore { get; set; } = null!;
    private BlockHeader? _manualFinalizedPoint = null;

    public ulong FinalizedBlockNumber
    {
        get
        {
            if (_manualFinalizedPoint is not null)
            {
                return _manualFinalizedPoint.Number;
            }
            return TrieStore.LatestCommittedBlockNumber.SaturatingSub(depth);
        }
    }

    public Hash256? GetFinalizedStateRootAt(ulong blockNumber)
    {
        if (_manualFinalizedPoint is not null && _manualFinalizedPoint.Number == blockNumber)
        {
            return _manualFinalizedPoint.StateRoot;
        }
        using ArrayPoolListRef<BlockCommitSet> commitSets = TrieStore.CommitSetQueue.GetCommitSetsAtBlockNumber(blockNumber);
        if (commitSets.Count != 1) return null;
        return commitSets[0].StateRoot;
    }

    public void SetFinalizedPoint(BlockHeader baseBlock) => _manualFinalizedPoint = baseBlock;
}
