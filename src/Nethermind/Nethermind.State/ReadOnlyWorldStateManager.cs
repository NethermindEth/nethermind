// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Mainly to make it easier for test
/// </summary>
public class ReadOnlyWorldStateManager : IWorldStateManager
{
    protected readonly IStateFactory StateFactory;
    private readonly ILogManager _logManager;
    private readonly ReadOnlyDb _codeDb;

    public ReadOnlyWorldStateManager(
        IDbProvider dbProvider,
        IStateFactory stateFactory,
        ILogManager logManager
    )
    {
        StateFactory = stateFactory;
        _logManager = logManager;

        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        _codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new StateReader(StateFactory, _codeDb, _logManager);
    }

    public virtual IWorldState GlobalWorldState => throw new InvalidOperationException("global world state not supported");

    public IStateReader GlobalStateReader { get; }

    public IWorldState CreateResettableWorldState(IWorldState? forWarmup = null)
    {
        PreBlockCaches? preBlockCaches = (forWarmup as IPreBlockCaches)?.Caches;
        return preBlockCaches is not null
            ? new WorldState(StateFactory,
                _codeDb,
                _logManager,
                preBlockCaches)
            : new WorldState(
                StateFactory,
                _codeDb,
                _logManager);
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }
}
