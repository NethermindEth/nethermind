// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateProvider: ReadOnlyWorldStateProvider
{
    private IWorldState _worldState { get; set; }

    public WorldStateProvider(
        IWorldState worldState,
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager) : base(dbProvider, trieStore.AsReadOnly(), logManager)
    {
        _worldState = worldState;
    }

    public override IWorldState GetWorldState(BlockHeader header)
    {
        // TODO: return corresponding worldState depending on header
        return _worldState;
    }
}
