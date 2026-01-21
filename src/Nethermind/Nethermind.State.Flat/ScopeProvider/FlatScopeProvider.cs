// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Org.BouncyCastle.Bcpg;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatScopeProvider : IWorldStateScopeProvider
{
    private readonly IFlatDbManager _flatDbManager;
    private readonly ILogManager _logManager;
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _trieWarmer;
    private readonly ResourcePool.Usage _usage;
    private FlatWorldStateScope? _activeScope;

    public FlatScopeProvider(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDbManager flatDbManager,
        IFlatDbConfig configuration,
        ITrieWarmer trieWarmer,
        ResourcePool.Usage usage,
        ILogManager logManager)
    {
        _flatDbManager = flatDbManager;
        _configuration = configuration;
        _trieWarmer = trieWarmer;
        _usage = usage;
        _logManager = logManager;
        _codeDb = new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(codeDb);
    }

    public bool HasRoot(BlockHeader? baseBlock)
    {
        return _flatDbManager.HasStateForBlock(new StateId(baseBlock));
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new StateId(baseBlock);
        SnapshotBundle snapshotBundle = _flatDbManager.GatherReaderAtBaseBlock(currentState, usage: _usage);
        if (_trieWarmer is NoopTrieWarmer) snapshotBundle.SetPrewarmer();

        ITrieWarmer warmer = _trieWarmer;
        if (_configuration.TrieWarmerWorkerCount == 0)
        {
            warmer = new NoopTrieWarmer();
        }

        FlatWorldStateScope scope = new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            _flatDbManager,
            _configuration,
            warmer,
            _logManager);

        _activeScope = scope;
        return scope;
    }

    public void ResetActiveScope()
    {
        _activeScope?.ResetState();
    }
}
