// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateManager(
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    FlatStateReader flatStateReader,
    ITrieWarmer trieWarmer,
    Func<FlatOverridableWorldScope> overridableWorldScopeFactory,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatStateRootIndex flatStateRootIndex,
    IStateHeaderProvider stateHeaderProvider,
    FlatTrieVerifier trieVerifier,
    Lazy<HalfPathMigrationCursor> migrationCursor,
    ILogManager logManager)
    : IWorldStateManager, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatWorldStateManager>();

    private readonly FlatScopeProvider _mainWorldState = new(
        codeDb,
        flatDbManager,
        configuration,
        trieWarmer,
        ResourcePool.Usage.MainBlockProcessing,
        stateHeaderProvider,
        logManager,
        isReadOnly: false);

    private SnapFlatStateServer? _snapServer;

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => flatStateReader;
    public ISnapStateServer SnapStateServer => _snapServer ??= new SnapFlatStateServer(
        flatDbManager,
        flatStateRootIndex,
        logManager);
    public IReadOnlyKeyValueStore? HashServer => null;

    public IWorldStateScopeProvider CreateResettableWorldState() => CreateReadOnlyScopeProvider(filterInMemorySlotReads: false);

    public IWorldStateScopeProvider CreateReadOnlyQueryWorldState() => CreateReadOnlyScopeProvider(filterInMemorySlotReads: true);

    private FlatScopeProvider CreateReadOnlyScopeProvider(bool filterInMemorySlotReads) =>
        new(
            codeDb,
            flatDbManager,
            configuration,
            new NoopTrieWarmer(),
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            stateHeaderProvider,
            logManager,
            isReadOnly: true,
            filterInMemorySlotReads: filterInMemorySlotReads);

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => new FlatReadOnlyTrieStore(flatDbManager);

    public IOverridableWorldScope CreateOverridableWorldScope() =>
        overridableWorldScopeFactory();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        // The flat iterators the verification compares against only see what the migration has copied.
        if (migrationCursor.Value.Current is not null)
        {
            if (_logger.IsWarn) _logger.Warn("The flat DB cannot be verified until its migration from the patricia trie state completes.");
            return false;
        }

        return trieVerifier.Verify(stateAtBlock, cancellationToken);
    }

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    public void DropStateNotReachableFrom(BlockHeader head) => flatDbManager.DropStateNotReachableFrom(new StateId(head));

    public void Dispose() => _mainWorldState.Dispose();
}
