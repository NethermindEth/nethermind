// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.FlatCache;

public class FlatCacheWorldStateManager(IWorldStateManager baseWorldStateManager, FlatCacheRepository cacheRepository, ILogManager logManager): IWorldStateManager
{
    FlatCacheScopeProvider _globalWorldState = new FlatCacheScopeProvider(baseWorldStateManager.GlobalWorldState, cacheRepository, isReadOnly: false, logManager);

    public IWorldStateScopeProvider GlobalWorldState => _globalWorldState;

    public IStateReader GlobalStateReader => baseWorldStateManager.GlobalStateReader;

    public ISnapServer? SnapServer => baseWorldStateManager.SnapServer;

    public IReadOnlyKeyValueStore? HashServer => baseWorldStateManager.HashServer;

    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        return new FlatCacheScopeProvider(baseWorldStateManager.CreateResettableWorldState(), cacheRepository, isReadOnly: true, logManager);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => baseWorldStateManager.ReorgBoundaryReached += value;
        remove => baseWorldStateManager.ReorgBoundaryReached -= value;
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        return baseWorldStateManager.CreateOverridableWorldScope();
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        return baseWorldStateManager.VerifyTrie(stateAtBlock, cancellationToken);
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        baseWorldStateManager.FlushCache(cancellationToken);
    }
}
