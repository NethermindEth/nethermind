// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
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
    IStateHeaderProvider stateHeaderProvider,
    ILogManager logManager,
    bool isReadOnly)
    : IWorldStateScopeProvider, IDisposable
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);
    private readonly IStateHeaderProvider _stateHeaderProvider = stateHeaderProvider;

    private readonly Lazy<WarmReadPool>? _warmReadPool = isReadOnly ? null : new Lazy<WarmReadPool>(() =>
    {
        int configured = configuration.WarmReadConcurrency;
        int concurrency = configured < 0 ? Math.Min(4 * Environment.ProcessorCount, 64) : Math.Max(1, configured);
        return new WarmReadPool(concurrency);
    });

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock), usage);

    // Trie verification makes every flat read also traverse the scope's storage trie, and
    // StorageTree/PatriciaTree traversal is not thread-safe, so background readers must not share a
    // scope's trees while it is on. The plain flat read path (snapshot bundles) is safe.
    public bool SupportsConcurrentScopes => !configuration.VerifyWithTrie;

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => this.HasRootForTarget(_stateHeaderProvider, targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        this.TryBeginScopeAtBase(_stateHeaderProvider, targetBlock, metrics, out scope);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle snapshotBundle;
        try
        {
            snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);
        }
        catch (StateUnavailableException)
        {
            scope = null;
            return false;
        }

        scope = new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            warmReadPool: _warmReadPool,
            isReadOnly: isReadOnly);
        return true;
    }

    public void Dispose()
    {
        if (_warmReadPool is { IsValueCreated: true }) _warmReadPool.Value.Dispose();
    }
}
