// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

/// <summary>
/// Fake <see cref="IFinalizedStateProvider"/> that simulate previous behaviour where it just check the
/// LatestCommittedBlockNumber minute depth. Not for prod use.
/// TrieStore must be set later.
/// </summary>
/// <param name="depth"></param>
public class TestFinalizedStateProvider(long depth) : IFinalizedStateProvider
{
    public TrieStore TrieStore { get; set; } = null!;
    private BlockHeader? _manualFinalizedPoint = null;

    public long FinalizedBlockNumber
    {
        get
        {
            if (_manualFinalizedPoint is not null)
            {
                return _manualFinalizedPoint.Number;
            }
            return TrieStore.LatestCommittedBlockNumber - depth;
        }
    }

    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        if (_manualFinalizedPoint is not null && _manualFinalizedPoint.Number == blockNumber)
        {
            return _manualFinalizedPoint.StateRoot;
        }
        using var commitSets = TrieStore.CommitSetQueue.GetCommitSetsAtBlockNumber(blockNumber);
        if (commitSets.Count != 1) return null;
        return commitSets[0].StateRoot;
    }

    public void SetFinalizedPoint(BlockHeader baseBlock)
    {
        _manualFinalizedPoint = baseBlock;
    }
}
