// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class PrewarmerEnvFactory(IWorldStateManager worldStateManager, ILogManager logManager, ILifetimeScope parentLifetime)
{
    public IPrewarmerEnv Create(PreBlockCaches preBlockCaches)
    {
        PrewarmerState prewarmerState = new(preBlockCaches, isPrewarmer: true);
        PrewarmerScopeProvider worldState = new(
            worldStateManager.CreateResettableWorldState(),
            prewarmerState,
            logManager
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IPrewarmerState>(prewarmerState)
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
        });

        try
        {
            return new PrewarmerEnv(
                childScope,
                childScope.Resolve<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>(),
                childScope.Resolve<IHasAccessList[]>());
        }
        catch
        {
            // A hint provider is plugin-replaceable and its constructor can throw; without this the scope and the
            // resettable world state it holds are left with no reference to dispose them.
            childScope.Dispose();
            throw;
        }
    }

    private sealed class PrewarmerEnv(ILifetimeScope scope, IReadOnlyTxProcessorSource inner, IHasAccessList[] systemAccessLists) : IPrewarmerEnv
    {
        public ReadOnlySpan<IHasAccessList> SystemAccessLists => systemAccessLists;

        public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) => inner.TryBuild(baseBlock, out scope);

        public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) => inner.TryBuildAtTarget(targetBlock, out scope);

        /// <remarks>Disposes the scope rather than just the inner env, so anything else resolved into it is released too.</remarks>
        public void Dispose() => scope.Dispose();
    }
}

/// <summary>
/// A prewarmer env: a read-only tx processor source plus the system-contract access-list hints bound to its own
/// world state.
/// </summary>
/// <remarks>
/// The hint providers read state (<c>AccountExists</c> / <c>IsContract</c>) to decide whether the system contract is
/// deployed, so they are only usable inside an open world-state scope. Resolving them from the env's lifetime scope
/// binds them to the env's world state, the one <see cref="IReadOnlyTxProcessorSource.TryBuild"/> opens a scope on. The
/// main processing world state has no scope open between blocks, which is exactly when the speculative pass runs.
/// </remarks>
public interface IPrewarmerEnv : IReadOnlyTxProcessorSource
{
    ReadOnlySpan<IHasAccessList> SystemAccessLists { get; }
}
