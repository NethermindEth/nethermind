// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State;

public class OverridableWorldStateManager : IResettableWorldStateManager
{
    private readonly ReadOnlyDbProvider _readOnlyDbProvider;
    private readonly ILogManager? _logManager;
    private readonly IStateFactory _factory;

    public OverridableWorldStateManager(IDbProvider dbProvider, IStateFactory factory, ILogManager? logManager)
    {
        _readOnlyDbProvider = new(dbProvider, true);
        _logManager = logManager;
        _factory = factory;

        GlobalStateReader = new StateReader(factory,dbProvider.GetDb<IDb>(DbNames.Code), logManager);
    }

    public IStateReader GlobalStateReader { get; }

    public IWorldState CreateResettableWorldState(IWorldState? forWarmup = null)
    {
        if (forWarmup is not null)
            throw new NotSupportedException("Overridable world state with warm up is not supported.");

        return new OverridableWorldState(_factory, _readOnlyDbProvider, _logManager);
    }
}
