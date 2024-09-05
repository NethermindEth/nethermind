// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateProvider : IWorldStateProvider
{
    private IWorldState _worldState;
    private IStateReader _stateReader;
    public ITrieStore TrieStore { get; }

    public OverlayWorldStateProvider(IWorldState worldState, IReadOnlyDbProvider dbProvider,
        OverlayTrieStore overlayTrieStore, ILogManager? logManager)
    {
        _worldState = worldState;
        _stateReader = new StateReader(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        TrieStore = overlayTrieStore;
    }
    public IWorldState GetWorldState(BlockHeader header)
    {
        return _worldState;
    }

    public IStateReader GetStateReader(Hash256 stateRoot)
    {
        return _stateReader;
    }
}
