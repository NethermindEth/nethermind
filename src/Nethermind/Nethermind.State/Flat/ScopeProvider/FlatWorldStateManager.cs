// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateManager : IWorldStateManager
{
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly FlatStateReader _flatStateReader;
    private readonly FlatScopeProvider _mainWorldState;
    private readonly IDb _codeDb;
    private readonly ILogManager _logManager;
    private readonly FlatDiffRepository.Configuration _configuration;

    public FlatWorldStateManager(
        IFlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        FlatStateReader flatStateReader,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        ILogManager logManager
    )
    {
        _flatDiffRepository = flatDiffRepository;
        _flatStateReader = flatStateReader;
        _codeDb = codeDb;
        _logManager = logManager;
        _configuration = configuration;
        _mainWorldState = new FlatScopeProvider(codeDb, flatDiffRepository, configuration, logManager);
    }

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => _flatStateReader;
    public ISnapServer? SnapServer => null;
    public IReadOnlyKeyValueStore? HashServer => null;
    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        return new FlatScopeProvider(_codeDb, _flatDiffRepository, _configuration, _logManager, isReadOnly: true);
    }

    event EventHandler<ReorgBoundaryReached>? IWorldStateManager.ReorgBoundaryReached
    {
        add => _flatDiffRepository.ReorgBoundaryReached += value;
        remove => _flatDiffRepository.ReorgBoundaryReached -= value;
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        var scopeProvider = new FlatScopeProvider(_codeDb, _flatDiffRepository, _configuration, _logManager, isReadOnly: true);
        return new FakeOverridableWorldScope(scopeProvider, _flatStateReader);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("Verify trie not implemented");
        return false;
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        _flatDiffRepository.FlushCache(cancellationToken);
    }

    public class FakeOverridableWorldScope(IWorldStateScopeProvider worldState, IStateReader stateReader) : IOverridableWorldScope
    {
        public IWorldStateScopeProvider WorldState => worldState;
        public IStateReader GlobalStateReader => stateReader;
        public void ResetOverrides()
        {
        }
    }
}
