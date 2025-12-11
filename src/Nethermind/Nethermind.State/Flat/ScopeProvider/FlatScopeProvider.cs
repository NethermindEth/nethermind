// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Org.BouncyCastle.Bcpg;
using Prometheus;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatScopeProvider : IWorldStateScopeProvider
{
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly ILogManager _logManager;
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb;
    private readonly bool _isReadOnly;
    private readonly FlatDiffRepository.Configuration _configuration;
    private readonly ITrieWarmer _trieWarmer;
    private readonly ResourcePool _resourcePool;
    private readonly IFlatDiffRepository.SnapshotBundleUsage _usage;

    public FlatScopeProvider(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        ITrieWarmer trieWarmer,
        ResourcePool resourcePool,
        IFlatDiffRepository.SnapshotBundleUsage usage,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _flatDiffRepository = flatDiffRepository;
        _configuration = configuration;
        _trieWarmer = trieWarmer;
        _resourcePool = resourcePool;
        _usage = usage;
        _logManager = logManager;
        _codeDb = new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(codeDb);
        _isReadOnly = isReadOnly;
    }

    public bool HasRoot(BlockHeader? baseBlock)
    {
        return _flatDiffRepository.HasStateForBlock(new StateId(baseBlock));
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new StateId(baseBlock);
        SnapshotBundle snapshotBundle = _flatDiffRepository.GatherReaderAtBaseBlock(currentState, usage: _usage);
        if (_trieWarmer is NoopTrieWarmer) snapshotBundle.SetPrewarmer();

        ITrieWarmer warmer = _trieWarmer;
        if (_configuration.DisableTrieWarmer)
        {
            warmer = new NoopTrieWarmer();
        }

        return new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            _flatDiffRepository,
            _configuration,
            warmer,
            _resourcePool,
            _logManager,
            _isReadOnly
        );
    }
}
