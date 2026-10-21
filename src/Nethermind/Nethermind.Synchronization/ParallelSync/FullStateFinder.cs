// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Synchronization.ParallelSync;

public class FullStateFinder(
    IBlockTree blockTree,
    IStateReader stateReader) : IFullStateFinder
{
    // TODO: we can search 1024 back and confirm 128 deep header and start using it as Max(0, confirmed)
    // then we will never have to look 128 back again
    // note that we will be doing that every second or so
    private const ulong MaxLookupBack = 128;
    private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
    private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

    private ulong _lastKnownState;

    private bool IsFullySynced(BlockHeader block) =>
        block.StateRoot == Keccak.EmptyTreeHash || _stateReader.HasStateForBlock(block);

    public ulong FindBestFullState()
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

        ulong bestFullState = 0;
        if (head is not null)
        {
            // head search should be very inexpensive as we generally expect the state to be there
            bestFullState = SearchForFullState(head.Header);
        }

        if (bestSuggested is not null)
        {
            if (bestFullState < bestSuggested.Number)
            {
                bestFullState = Math.Max(bestFullState, SearchForFullState(bestSuggested));
            }
        }

        if (bestFullState != 0)
        {
            _lastKnownState = bestFullState;
        }

        return bestFullState;
    }

    private ulong SearchForFullState(BlockHeader startHeader)
    {
        ulong bestFullState = 0;
        ulong maxLookupBack = MaxLookupBack;
        if (_lastKnownState != 0 && startHeader.Number >= _lastKnownState)
        {
            ulong lookback = startHeader.Number - _lastKnownState + 1;
            if (lookback > maxLookupBack) maxLookupBack = lookback;
        }

        for (ulong i = 0; i < maxLookupBack; i++)
        {
            if (startHeader is null)
            {
                break;
            }

            if (IsFullySynced(startHeader))
            {
                bestFullState = startHeader.Number;
                break;
            }

            startHeader = _blockTree.FindParentHeader(startHeader!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        return bestFullState;
    }
}
