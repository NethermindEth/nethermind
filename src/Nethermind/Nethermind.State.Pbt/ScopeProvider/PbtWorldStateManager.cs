// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtWorldStateManager(
    IPbtDbManager manager,
    IPbtChildHeaderSource childHeaders,
    IPbtResourcePool resourcePool,
    PbtStateReader stateReader,
    Func<PbtOverridableWorldScope> overridableWorldScopeFactory,
    IPbtConfig config,
    ITrieWarmer trieWarmer,
    [KeyFilter(DbNames.Code)] IDb codeDb) : IWorldStateManager
{
    private readonly PbtTrieLayout _writeLayout = config.TrieNodeLayout;
    private readonly int _rootFoldConcurrency = config.RootFoldConcurrency;
    private readonly PbtScopeProvider _mainWorldState = new(codeDb, manager, childHeaders, resourcePool, PbtResourcePool.Usage.MainBlockProcessing, isReadOnly: false, config.TrieNodeLayout, config.RootFoldConcurrency, trieWarmer);

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;

    public IStateReader GlobalStateReader => stateReader;

    public ISnapServer SnapServer => NoopSnapServer.Instance;

    public IReadOnlyKeyValueStore? HashServer => null;

    public IWorldStateScopeProvider CreateResettableWorldState() => new PbtScopeProvider(codeDb, manager, childHeaders, resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: true, _writeLayout, _rootFoldConcurrency, trieWarmer);

    public IOverridableWorldScope CreateOverridableWorldScope() => overridableWorldScopeFactory();

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => new PbtUnsupportedReadOnlyTrieStore();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => true;

    public void FlushCache(CancellationToken cancellationToken) => manager.FlushCache(cancellationToken);
}
