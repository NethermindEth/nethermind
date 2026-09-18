// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

#pragma warning disable IDE0130 // Production namespace on purpose: tests open scopes with the throwing shape everywhere, while production code has to go through TryBeginScope
namespace Nethermind.Evm.State;
#pragma warning restore IDE0130

public static class WorldStateScopeExtensions
{
    /// <inheritdoc cref="IWorldState.TryBeginScope"/>
    /// <exception cref="InvalidOperationException">The state at <paramref name="baseBlock"/> is unavailable.</exception>
    public static IDisposable BeginScope(this IWorldState worldState, BlockHeader? baseBlock) =>
        worldState.TryBeginScope(baseBlock, out IDisposable? scopeCloser) ? scopeCloser : ThrowUnavailable<IDisposable>(baseBlock);

    /// <inheritdoc cref="IWorldStateScopeProvider.TryBeginScope"/>
    /// <exception cref="InvalidOperationException">The state at <paramref name="baseBlock"/> is unavailable.</exception>
    public static IWorldStateScopeProvider.IScope BeginScope(this IWorldStateScopeProvider scopeProvider, BlockHeader? baseBlock, LocalMetrics metrics) =>
        scopeProvider.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? scope) ? scope : ThrowUnavailable<IWorldStateScopeProvider.IScope>(baseBlock);

    private static TScope ThrowUnavailable<TScope>(BlockHeader? baseBlock) =>
        throw new InvalidOperationException($"State is unavailable for base block {baseBlock?.ToString(BlockHeader.Format.Short) ?? "pre-genesis"}.");
}
