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
    IOldestStateBlockStore oldestStateBlockStore,
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
    // is reported through the OldestStateBlock floor instead.
    public long? GetOldestStateBlock(long headBlock) => null;

    public long? OldestStateBlock
    {
        get => oldestStateBlockStore.OldestStateBlock;
        set => oldestStateBlockStore.OldestStateBlock = value;
    }

    // Flat storage can technically reconstruct proofs from flat data, but only for blocks where
    // a flat snapshot still exists and the reconstruction is O(state-size). Treat it as not
    // serving trie proofs for routing purposes — eth_getProof callers expect cheap responses.
    public bool SupportsTrieProofs => false;

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
