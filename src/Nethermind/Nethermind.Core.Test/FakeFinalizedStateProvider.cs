// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

public class FakeFinalizedStateProvider(long depth): IFinalizedStateProvider
{
    public TrieStore TrieStore { get; set; } = null!;

    public long FinalizedBlockNumber => TrieStore.LatestCommittedBlockNumber - depth;
    public Hash256? GetFinalizedStateRootAt(long blockNumber)
    {
        using var commitSets = TrieStore.CommitSetQueue.GetCommitSetsAtBlockNumber(blockNumber);
        if (commitSets.Count != 1) return null;
        return commitSets[0].StateRoot;
    }
}
