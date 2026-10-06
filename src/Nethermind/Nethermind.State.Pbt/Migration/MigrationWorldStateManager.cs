// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Composes the native flat and PBT managers into the single world state the node processes on.</summary>
internal sealed class MigrationWorldStateManager(
    FlatWorldStateManager flat,
    PbtWorldStateManager pbt,
    MigrationScopeProvider mainWorldState,
    MigrationStateReader stateReader,
    Func<MigrationOverridableWorldScope> overridableWorldScopeFactory,
    ISpecProvider specProvider) : IWorldStateManager
{
    public IWorldStateScopeProvider GlobalWorldState => mainWorldState;
    public IStateReader GlobalStateReader => stateReader;
    public ISnapStateServer SnapStateServer => NoopSnapServer.Instance;
    public IReadOnlyKeyValueStore? HashServer => null;

    public IWorldStateScopeProvider CreateResettableWorldState() =>
        new MigrationReadOnlyScopeProvider(flat.CreateResettableWorldState(), pbt.CreateResettableWorldState(), specProvider);

    public IOverridableWorldScope CreateOverridableWorldScope() => overridableWorldScopeFactory();

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => flat.CreateReadOnlyTrieStore();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) =>
        specProvider.GetSpec(stateAtBlock).IsEip8347Enabled
            ? pbt.VerifyTrie(stateAtBlock, cancellationToken)
            : flat.VerifyTrie(stateAtBlock, cancellationToken);

    public void FlushCache(CancellationToken cancellationToken)
    {
        flat.FlushCache(cancellationToken);
        pbt.FlushCache(cancellationToken);
    }

    public void DropStateNotReachableFrom(BlockHeader head)
    {
        flat.DropStateNotReachableFrom(head);
        pbt.DropStateNotReachableFrom(head);
    }
}
