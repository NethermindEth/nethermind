// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager : IPreBlockCaches
{

    // dod we need this here, now that we are going to use worldState per use case?
    IWorldState GlobalWorldState { get; }

    // i think we can leave it as it is as we need only one StateReader as we always send state root to get the satate
    // from state reader - so we will not need different state reader per block?
    IStateReader GlobalStateReader { get; }
    IReadOnlyTrieStore TrieStore { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <param name="header"></param>
    /// <param name="forWarmup">Specify a world state to warm up by the returned world state.</param>
    /// <returns></returns>
    IWorldState CreateResettableWorldState(BlockHeader header);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    IWorldState GetGlobalWorldState(BlockHeader blockHeader);

    bool ClearCache();

    bool HasStateRoot(Hash256 root);

    public void Accept(ITreeVisitor treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) =>
        GlobalStateReader.RunTreeVisitor(treeVisitor, stateRoot, visitingOptions);

}
