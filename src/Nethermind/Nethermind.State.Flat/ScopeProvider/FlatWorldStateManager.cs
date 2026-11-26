// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateManager(
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    FlatStateReader flatStateReader,
    ITrieWarmer trieWarmer,
    Func<FlatOverridableWorldScope> overridableWorldScopeFactory,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IStateHeaderProvider stateHeaderProvider,
    FlatTrieVerifier trieVerifier,
    ILogManager logManager)
    : IWorldStateManager, IDisposable
{
    private readonly FlatScopeProvider _mainWorldState = new(
        codeDb,
        flatDbManager,
        configuration,
        trieWarmer,
        ResourcePool.Usage.MainBlockProcessing,
        stateHeaderProvider,
        logManager,
        isReadOnly: false);

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => flatStateReader;
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

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) =>
        trieVerifier.Verify(stateAtBlock, cancellationToken);

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    public void DropStateNotReachableFrom(BlockHeader head) => flatDbManager.DropStateNotReachableFrom(new StateId(head));

    public void Dispose() => _mainWorldState.Dispose();
}
