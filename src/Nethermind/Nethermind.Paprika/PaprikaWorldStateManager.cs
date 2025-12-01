// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;
using Paprika.Merkle;

namespace Nethermind.Paprika;

public class PaprikaWorldStateManager: IWorldStateManager, IAsyncDisposable
{
    private global::Paprika.Chain.Blockchain _paprikaBlockchain;
    private global::Paprika.IDb _paprikaDb;
    private PaprikaStateReader _stateReader;

    private ComputeMerkleBehavior _merkleBehavior;
    private IWorldStateScopeProvider _mainScopeProvider;
    private readonly IDb _codeDb;
    private SemaphoreSlim _scopeLock = new SemaphoreSlim(1);

    public PaprikaWorldStateManager(
        global::Paprika.IDb paprikaDb,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        ComputeMerkleBehavior computeMerkleBehavior,
        global::Paprika.Chain.Blockchain paprikaBlockchain)
    {
        _paprikaDb = paprikaDb;
        _codeDb = codeDb;
        _merkleBehavior = computeMerkleBehavior;
        _paprikaBlockchain = paprikaBlockchain;

        var readOnlyWorldStateAccessor = _paprikaBlockchain.BuildReadOnlyAccessor();
        _stateReader = new PaprikaStateReader(readOnlyWorldStateAccessor, codeDb);

        _mainScopeProvider =
            new PaprikaWorldStateScopeProvider(_paprikaBlockchain, codeDb, _scopeLock);
    }

    public IWorldStateScopeProvider GlobalWorldState => _mainScopeProvider;
    public IStateReader GlobalStateReader => _stateReader;
    public ISnapServer? SnapServer => null;
    public IReadOnlyKeyValueStore? HashServer => null;
    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        return new PaprikaReadOnlyStateScopeProvider(_paprikaBlockchain, _codeDb, _scopeLock);
    }

#pragma warning disable CS0067
    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached; // TODO: How?
#pragma warning restore CS0067
    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        return new PaprikaOverridableWorldScope(
            new PaprikaReadOnlyStateScopeProvider(_paprikaBlockchain, _codeDb, _scopeLock), _stateReader);

    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        // TODO: Flush
    }

    private bool _wasDisposed = false;

    public ValueTask DisposeAsync()
    {
        if (_wasDisposed) return ValueTask.CompletedTask;
        _wasDisposed = true;
        _merkleBehavior.Dispose();
        return ValueTask.CompletedTask;
    }

    // Just a fake one
    private class PaprikaOverridableWorldScope(IWorldStateScopeProvider worldStateScopeProvider, IStateReader stateReader) : IOverridableWorldScope
    {
        public IWorldStateScopeProvider WorldState => worldStateScopeProvider;
        public IStateReader GlobalStateReader => stateReader;
        public void ResetOverrides()
        {
        }
    }
}
