// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
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
    public ISnapServer? SnapServer => _snapServer ??= new FlatSnapServer(
        flatDbManager,
        new ReadOnlyDb(codeDb, true),
        flatStateRootIndex,
        logManager);
    public IReadOnlyKeyValueStore? HashServer => null;

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

    public IOverridableWorldScope CreateOverridableWorldScope() =>
        overridableWorldScopeFactory();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) =>
        _trieVerifier.Verify(stateAtBlock, cancellationToken);

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);
}
