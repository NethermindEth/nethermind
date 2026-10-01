// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Owns the isolated state environment used to verify a BAL before applying it to main state.</summary>
public sealed class FinalizedBlockAccessListVerificationEnv : IDisposable
{
    private readonly ILifetimeScope _scope;
    private readonly IWorldState _state;

    /// <summary>Creates a verification scope using the configured state backend.</summary>
    public FinalizedBlockAccessListVerificationEnv(ILifetimeScope lifetimeScope, IWorldStateManager worldStateManager)
    {
        _scope = lifetimeScope.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(worldStateManager.CreateResettableWorldState()));
        try
        {
            _state = _scope.Resolve<IWorldState>();
        }
        catch
        {
            _scope.Dispose();
            throw;
        }
    }

    /// <summary>Checks the post-state root without changing the main processing state.</summary>
    public bool Verify(Block block, ReadOnlyBlockAccessList list, IReleaseSpec spec, CancellationToken token)
    {
        if (!_state.TryBeginScopeAtTarget(block.Header, out IDisposable? closer)) return false;
        using (closer)
        {
            BlockAccessListStateReconstructor.Apply(_state, list, spec, token);
            _state.Commit(spec);
            _state.RecalculateStateRoot();
            return _state.StateRoot == block.StateRoot;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _scope.Dispose();
}
