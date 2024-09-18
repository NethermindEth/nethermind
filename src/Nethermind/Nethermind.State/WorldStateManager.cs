// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager(
    WorldStateProvider worldStateProvider,
    IDbProvider dbProvider,
    ITrieStore trieStore,
    ILogManager logManager,
    PreBlockCaches? preBlockCaches = null)
    : ReadOnlyWorldStateManager(worldStateProvider, dbProvider, trieStore.AsReadOnly(), logManager, preBlockCaches)
{
    public override IWorldStateProvider GlobalWorldStateProvider => worldStateProvider;

    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => trieStore.ReorgBoundaryReached += value;
        remove => trieStore.ReorgBoundaryReached -= value;
    }
}
