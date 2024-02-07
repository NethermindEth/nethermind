// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.ParallelSync;

public class FullStateFinder : IFullStateFinder
{
    // TODO: we can search 1024 back and confirm 128 deep header and start using it as Max(0, confirmed)
    // then we will never have to look 128 back again
    // note that we will be doing that every second or so
    private const int MaxLookupBack = 192;
    private readonly IStateReader _stateReader;
    private readonly IBlockTree _blockTree;

    public FullStateFinder(
        IBlockTree blockTree,
        IStateReader stateReader)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
    }

    private bool IsFullySynced(Hash256 stateRoot)
    {
        if (stateRoot == Keccak.EmptyTreeHash)
        {
            return true;
        }

        return _stateReader.HasStateForRoot(stateRoot);
    }

    public long FindBestFullState()
    {
        // so the full state can be in a few places but there are some best guesses
        // if we are state syncing then the full state may be one of the recent blocks (maybe one of the last 128 blocks)
        // if we full syncing then the state should be at head
        // it also may seem tricky if best suggested is part of a reorg while we are already full syncing so
        // ideally we would like to check its siblings too (but this may be a bit expensive and less likely
        // to be important
        // we want to avoid a scenario where state is not found even as it is just near head or best suggested

        Block head = _blockTree.Head;
        BlockHeader initialBestSuggested = _blockTree.BestSuggestedHeader; // just storing here for debugging sake
        BlockHeader bestSuggested = initialBestSuggested;

        long bestFullState = 0;
        if (head is not null)
        {
            // head search should be very inexpensive as we generally expect the state to be there
            bestFullState = SearchForFullState(head.Header);
        }

        if (bestSuggested is not null)
        {
            if (bestFullState < bestSuggested?.Number)
            {
                bestFullState = Math.Max(bestFullState, SearchForFullState(bestSuggested));
            }
        }

        return bestFullState;
    }

    private long SearchForFullState(BlockHeader startHeader)
    {
        long bestFullState = 0;
        for (int i = 0; i < MaxLookupBack; i++)
        {
            if (startHeader is null)
            {
                break;
            }

            if (IsFullySynced(startHeader.StateRoot!))
            {
                bestFullState = startHeader.Number;
                break;
            }

            startHeader = _blockTree.FindHeader(startHeader.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        return bestFullState;
    }

}
