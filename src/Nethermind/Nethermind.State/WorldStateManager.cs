// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager : ReadOnlyWorldStateManager
{
    private readonly IWorldState _worldState;
    private readonly ITrieStore _trieStore;

    public WorldStateManager(
        [KeyFilter(ComponentKey.MainWorldState)] IWorldState worldState,
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager
    ) : base(dbProvider, trieStore.AsReadOnly(), logManager)
    {
        _worldState = worldState;
        _trieStore = trieStore;
    }

    public override IWorldState GlobalWorldState => _worldState;

    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _trieStore.ReorgBoundaryReached += value;
        remove => _trieStore.ReorgBoundaryReached -= value;
    }
}
