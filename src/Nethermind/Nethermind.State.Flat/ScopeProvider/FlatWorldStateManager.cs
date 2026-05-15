// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateManager(
    IFlatDbManager flatDbManager,
    IPersistence persistence,
    IFlatDbConfig configuration,
    FlatStateReader flatStateReader,
    ITrieWarmer trieWarmer,
    Func<FlatOverridableWorldScope> overridableWorldScopeFactory,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatStateRootIndex flatStateRootIndex,
    IColumnsDb<FlatDbColumns> flatDb,
    ILogManager logManager)
    : IWorldStateManager
{
    private readonly FlatScopeProvider _mainWorldState = new(
        codeDb,
        flatDbManager,
        configuration,
        trieWarmer,
        ResourcePool.Usage.MainBlockProcessing,
        logManager,
        isReadOnly: false);

    private readonly FlatTrieVerifier _trieVerifier = new(flatDbManager, persistence, logManager);

    // OldestStateBlock co-located with state in the flat metadata column.
    private readonly StateBoundaryStore _boundaryStore = new(flatDb.GetColumnDb(FlatDbColumns.Metadata));

    private FlatSnapServer? _snapServer;

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => flatStateReader;
    public ISnapServer SnapServer => _snapServer ??= new FlatSnapServer(
        flatDbManager,
        codeDb,
        flatStateRootIndex,
        logManager);
    public IReadOnlyKeyValueStore? HashServer => null;

    // No memory-pruning rolling window. State retention is driven by snapshot persistence and
    // is reported through OldestStateBlock instead.
    public long? RetentionWindowBlocks => null;

    public long? OldestStateBlock
    {
        get => _boundaryStore.OldestStateBlock;
        set => _boundaryStore.OldestStateBlock = value;
    }

    public IWorldStateScopeProvider CreateResettableWorldState() =>
        new FlatScopeProvider(
            codeDb,
            flatDbManager,
            configuration,
            new NoopTrieWarmer(),
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            logManager,
            isReadOnly: true);

    event EventHandler<ReorgBoundaryReached>? IWorldStateManager.ReorgBoundaryReached
    {
        add => flatDbManager.ReorgBoundaryReached += value;
        remove => flatDbManager.ReorgBoundaryReached -= value;
    }

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => new FlatReadOnlyTrieStore(flatDbManager);

    public IOverridableWorldScope CreateOverridableWorldScope() =>
        overridableWorldScopeFactory();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) =>
        _trieVerifier.Verify(stateAtBlock, cancellationToken);

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);
}
