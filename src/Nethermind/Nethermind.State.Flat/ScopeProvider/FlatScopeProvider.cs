// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatScopeProvider(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    ITrieWarmer trieWarmer,
    ResourcePool.Usage usage,
    ILogManager logManager,
    bool isReadOnly,
    NodeStorageCache? nodeStorageCache = null)
    : IWorldStateScopeProvider, IDisposable
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);
    private readonly PreservedPatriciaTrie? _preservedPatriciaTrie = configuration.PreservePatriciaTrie && !isReadOnly
        ? new PreservedPatriciaTrie()
        : null;
    private readonly PreservedStorageTries? _preservedStorageTries = configuration.PreservePatriciaTrie && !isReadOnly
        ? new PreservedStorageTries()
        : null;

    private readonly Lazy<WarmReadPool>? _warmReadPool = isReadOnly ? null : new Lazy<WarmReadPool>(() =>
    {
        int configured = configuration.WarmReadConcurrency;
        int concurrency = configured < 0 ? Math.Min(4 * Environment.ProcessorCount, 64) : Math.Max(1, configured);
        return new WarmReadPool(concurrency);
    });

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);
        if (nodeStorageCache is not null)
        {
            snapshotBundle.AttachNodeStorageCache(nodeStorageCache);
        }

        return new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            warmReadPool: _warmReadPool,
            preservedPatriciaTrie: _preservedPatriciaTrie,
            preservedStorageTries: _preservedStorageTries,
            isReadOnly: isReadOnly);
    }

    public void Dispose()
    {
        if (_warmReadPool is { IsValueCreated: true }) _warmReadPool.Value.Dispose();
    }
}
