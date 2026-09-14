// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

/// <summary>
/// Fake <see cref="IParentHeaderProvider"/> that simulate previous behaviour where it just check the
/// LatestCommittedBlockNumber minute depth. Not for prod use.
/// TrieStore must be set later.
/// </summary>
/// <param name="depth"></param>
public class TestFinalizedStateProvider(ulong depth) : IParentHeaderProvider
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

    public BlockHeader? GetFinalizedHeader(ulong blockNumber)
    {
        if (_manualFinalizedPoint is not null && _manualFinalizedPoint.Number == blockNumber)
        {
            return _manualFinalizedPoint;
        }
        using ArrayPoolListRef<BlockCommitSet> commitSets = TrieStore.CommitSetQueue.GetCommitSetsAtBlockNumber(blockNumber);
        if (commitSets.Count != 1) return null;
        return FinalizedHeader(blockNumber, commitSets[0].StateRoot);
    }

    public BlockHeader? FindParentHeader(BlockHeader target) => throw new InvalidOperationException("Parent lookup is not supported by this test finality provider.");

    public static BlockHeader FinalizedHeader(ulong blockNumber, Hash256 stateRoot) => new(Keccak.EmptyTreeHash, Keccak.EmptyTreeHash, Address.Zero, UInt256.Zero, blockNumber, 30_000_000, 0, []);

    public void SetFinalizedPoint(BlockHeader baseBlock) => _manualFinalizedPoint = baseBlock;
}
