// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateProvider : IWorldStateProvider
{
    private IWorldState WorldState { get; set; }
    private Hash256 OriginalStateRoot { get; set; }
    private IStateReader StateReader { get; set; }
    public ITrieStore TrieStore { get; }

    public OverlayWorldStateProvider(IWorldState worldState, IReadOnlyDbProvider dbProvider,
        OverlayTrieStore overlayTrieStore, ILogManager? logManager)
    {
        WorldState = worldState;
        StateReader = new StateReader(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        TrieStore = overlayTrieStore;
    }
    public IWorldState GetGlobalWorldState(BlockHeader header)
    {
        return WorldState;
    }

    public IWorldState GetGlobalWorldState(UInt256 blockNumber)
    {
        // TODO: return corresponding worldState depending on blockNumber
        return WorldState;
    }

    public IStateReader GetGlobalStateReader()
    {
        return StateReader;
    }

    public IWorldState GetWorldState()
    {
        return WorldState;
    }

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        return StateReader.HasStateForRoot(stateRoot);
    }

    public void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null)
    {
        StateReader.RunTreeVisitor(treeVisitor, stateRoot, visitingOptions);
    }

    public void SetStateRoot(Hash256 stateRoot)
    {
        OriginalStateRoot = WorldState.StateRoot;
        WorldState.StateRoot = stateRoot;
    }

    public void Reset()
    {
        WorldState.StateRoot = OriginalStateRoot;
        WorldState.Reset();
    }
}
