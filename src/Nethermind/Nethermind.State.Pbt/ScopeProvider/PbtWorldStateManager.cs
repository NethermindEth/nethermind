// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtWorldStateManager(
    IPbtDbManager manager,
    IPbtChildHeaderSource childHeaders,
    IStateHeaderProvider stateHeaderProvider,
    IRefCountingMemoryProvider nodeGroupMemory,
    PbtStateReader stateReader,
    Func<PbtOverridableWorldScope> overridableWorldScopeFactory,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IPbtConfig config,
    ILogManager logManager) : IWorldStateManager
{
    private readonly PbtScopeProvider _mainWorldState = new(codeDb, manager, childHeaders, stateHeaderProvider, nodeGroupMemory, isReadOnly: false, config, logManager);

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;

    public IStateReader GlobalStateReader => stateReader;

    public ISnapStateServer SnapStateServer => NoopSnapServer.Instance;

    public IReadOnlyKeyValueStore? HashServer => null;

    public IWorldStateScopeProvider CreateResettableWorldState() => new PbtScopeProvider(codeDb, manager, childHeaders, stateHeaderProvider, nodeGroupMemory, isReadOnly: true, config, logManager);

    public IOverridableWorldScope CreateOverridableWorldScope() => overridableWorldScopeFactory();

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => new PbtUnsupportedReadOnlyTrieStore();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => true;

    public void FlushCache(CancellationToken cancellationToken) => manager.FlushCache(cancellationToken);

    public void DropStateNotReachableFrom(BlockHeader head) => manager.DropStateNotReachableFrom(new Flat.StateId(head));
}
