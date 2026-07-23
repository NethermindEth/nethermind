// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatScopeProvider(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    ITrieWarmer trieWarmer,
    ResourcePool.Usage usage,
    ILogManager logManager,
    bool isReadOnly)
    : IWorldStateScopeProvider, IDisposable
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);

    // Sparse-trie retention is an opt-in experiment gated by an env var (not a config key, per the
    // plan) until it shares one memory envelope with TrieNodeCache; off by default it never claims
    // memory beyond the node cache. Only the writable main-processing provider retains a
    // generation between blocks; read-only, historical, overridable, and tracing providers never do.
    private static readonly bool s_retentionEnabled =
        Environment.GetEnvironmentVariable("NETHERMIND_SPARSE_TRIE_RETENTION") == "1";

    private readonly FlatSparseTrieCache? _sparseCache =
        s_retentionEnabled && !isReadOnly && usage == ResourcePool.Usage.MainBlockProcessing
            ? new FlatSparseTrieCache(configuration.TrieCacheMemoryBudget)
            : null;

    private readonly Lazy<WarmReadPool>? _warmReadPool = isReadOnly ? null : new Lazy<WarmReadPool>(() =>
    {
        int configured = configuration.WarmReadConcurrency;
        int concurrency = configured < 0 ? Math.Min(4 * Environment.ProcessorCount, 64) : Math.Max(1, configured);
        return new WarmReadPool(concurrency);
    });

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);

        return new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            warmReadPool: _warmReadPool,
            isReadOnly: isReadOnly,
            sparseCache: _sparseCache);
    }

    public void Dispose()
    {
        if (_warmReadPool is { IsValueCreated: true }) _warmReadPool.Value.Dispose();
        _sparseCache?.Dispose();
    }
}
