// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager(
    IWorldState worldState,
    ITrieStore trieStore,
    IDbProvider dbProvider,
    ILogManager logManager,
    PreBlockCaches? preBlockCaches = null)
    : ReadOnlyWorldStateManager(dbProvider, trieStore.AsReadOnly(), logManager, preBlockCaches)
{
    public override IWorldState GlobalWorldState => worldState;

    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => trieStore.ReorgBoundaryReached += value;
        remove => trieStore.ReorgBoundaryReached -= value;
    }

    public override IWorldState GetGlobalWorldState(BlockHeader blockHeader) => GlobalWorldState;
}
