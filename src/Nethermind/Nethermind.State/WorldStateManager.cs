// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager: ReadOnlyWorldStateManager
{
    private readonly IWorldState _worldState;

    public WorldStateManager(
        IWorldState worldState,
        IDbProvider dbProvider,
        IReadOnlyTrieStore? readOnlyTrieStore,
        ILogManager logManager
    ) : base(dbProvider, readOnlyTrieStore, logManager)
    {
        _worldState = worldState;
    }

    public override IWorldState GlobalWorldState => _worldState;
}
