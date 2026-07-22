// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Pbt;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtWorldStateManager(
    IPbtDbManager manager,
    IPbtResourcePool resourcePool,
    PbtStateReader stateReader,
    Func<PbtOverridableWorldScope> overridableWorldScopeFactory,
    IPbtConfig config,
    [KeyFilter(DbNames.Code)] IDb codeDb) : IWorldStateManager
{
    private readonly PbtGroupFormat _writeFormat = config.TrieNodeWriteFormat();
    private readonly int _rootFoldConcurrency = config.RootFoldConcurrency;
    private readonly PbtScopeProvider _mainWorldState = new(codeDb, manager, resourcePool, PbtResourcePool.Usage.MainBlockProcessing, isReadOnly: false, config.TrieNodeWriteFormat(), config.RootFoldConcurrency);

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;

    public IStateReader GlobalStateReader => stateReader;

    public ISnapServer SnapServer => NoopSnapServer.Instance;

    public IReadOnlyKeyValueStore? HashServer => null;

    public IWorldStateScopeProvider CreateResettableWorldState() => new PbtScopeProvider(codeDb, manager, resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: true, _writeFormat, _rootFoldConcurrency);

    public IOverridableWorldScope CreateOverridableWorldScope() => overridableWorldScopeFactory();

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => new PbtUnsupportedReadOnlyTrieStore();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => true;

    public void FlushCache(CancellationToken cancellationToken) => manager.FlushCache(cancellationToken);
}
