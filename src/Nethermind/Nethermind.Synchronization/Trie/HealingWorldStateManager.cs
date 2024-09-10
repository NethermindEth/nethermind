// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingWorldStateManager(
    HealingWorldStateProvider worldStateProvider,
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
