// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public PrewarmerEnv Create(PreBlockCaches preBlockCaches)
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

        return new PrewarmerEnv(
            childScope.Resolve<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>(),
            childScope.Resolve<IHasAccessList[]>());
    }
}

/// <summary>
/// A prewarmer env together with the system-contract access-list hints bound to that env's own world state.
/// </summary>
/// <remarks>
/// The hint providers read state (<c>AccountExists</c> / <c>IsContract</c>) to decide whether the system contract is
/// deployed, so they are only usable inside an open world-state scope. Resolving them from the env's lifetime scope
/// binds them to the env's world state, the one <see cref="Build"/> opens a scope on. The main processing world state
/// has no scope open between blocks, which is exactly when the speculative pass evaluates them.
/// </remarks>
public sealed class PrewarmerEnv(IReadOnlyTxProcessorSource inner, IHasAccessList[] systemAccessLists) : IReadOnlyTxProcessorSource
{
    public IHasAccessList[] SystemAccessLists { get; } = systemAccessLists;

    public IReadOnlyTxProcessingScope Build(BlockHeader? header) => inner.Build(header);

    public void Dispose() => inner.Dispose();
}
