// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.ParallelSync;

public class FullStateFinder : IFullStateFinder
{
    // TODO: we can search 1024 back and confirm 128 deep header and start using it as Max(0, confirmed)
    // then we will never have to look 128 back again
    // note that we will be doing that every second or so
    private const int MaxLookupBack = 192;
    private readonly IDb _stateDb;
    private readonly ITrieNodeResolver _trieNodeResolver;
    private readonly IBlockTree _blockTree;

    public FullStateFinder(
        IBlockTree blockTree,
        IDb stateDb,
        ITrieNodeResolver trieNodeResolver)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
        _trieNodeResolver = trieNodeResolver ?? throw new ArgumentNullException(nameof(trieNodeResolver));
    }

    private bool IsFullySynced(Hash256 stateRoot)
    {
        if (stateRoot == Keccak.EmptyTreeHash)
        {
            return true;
        }

        // We check whether one of below happened:
        //   1) the block has been processed but not yet persisted (pruning) OR
        //   2) the block has been persisted and removed from cache already OR
        //   3) the full block state has been synced in the state nodes sync (fast sync)
        // In 2) and 3) the state root will be saved in the database.
        // In fast sync we never save the state root unless all the descendant nodes have been stored in the DB.

        bool stateRootIsInMemory = false;
        bool isPersisted = false;

        switch (_trieNodeResolver.Capability)
        {
            case TrieNodeResolverCapability.Hash:
            {
                stateRootIsInMemory = _trieNodeResolver.FindCachedOrUnknown(stateRoot).NodeType != NodeType.Unknown;
                isPersisted = _stateDb.Get(stateRoot) is not null;
                break;
            }
            case TrieNodeResolverCapability.Path:
            {
                stateRootIsInMemory = _trieNodeResolver.FindCachedOrUnknown(stateRoot, Span<byte>.Empty, Span<byte>.Empty).NodeType != NodeType.Unknown;
                isPersisted = _trieNodeResolver.IsPersisted(stateRoot, Array.Empty<byte>());
                break;
            }
        }

        return stateRootIsInMemory || isPersisted;
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
