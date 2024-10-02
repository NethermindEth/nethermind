// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager(
    IWorldState worldState,
    IStateFactory stateFactory,
    IDbProvider dbProvider,
    ILogManager logManager)
    : ReadOnlyWorldStateManager(dbProvider, stateFactory, logManager)
{
    public override IWorldState GlobalWorldState => worldState;

    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => StateFactory.ReorgBoundaryReached += value;
        remove => StateFactory.ReorgBoundaryReached -= value;
    }
}
