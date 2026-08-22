// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
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

    // Retention is the normal main-processing path; the environment switch remains as an
    // operational escape hatch while the sparse pipeline is being rolled out.
    private readonly FlatSparseTrieCache? _sparseCache =
        SparseTrieRetention.Enabled && !isReadOnly && usage == ResourcePool.Usage.MainBlockProcessing
            ? new FlatSparseTrieCache(SparseTrieRetention.GetSparseBudget(configuration.TrieCacheMemoryBudget))
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
            sparseCache: _sparseCache,
            // The worker thread drains warm-up prefetches whether or not committed values stream to it.
            rootWorker: _sparseCache?.RootWorker,
            streamCommittedValues: SparseTrieRetention.ConcurrentRootEnabled);
    }

    public void Dispose()
    {
        if (_warmReadPool is { IsValueCreated: true }) _warmReadPool.Value.Dispose();
        if (_sparseCache is not null) LogRetentionStatsAndDispose(_sparseCache);
    }

    // Out of line so the cold shutdown log does not bloat Dispose.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogRetentionStatsAndDispose(FlatSparseTrieCache cache)
    {
        ILogger logger = logManager.GetClassLogger<FlatScopeProvider>();
        if (logger.IsInfo)
            logger.Info($"[sparse-retention] checkout hits {cache.Hits}, misses {cache.Misses}, rejections {cache.Rejections}");
        cache.Dispose();
    }
}
