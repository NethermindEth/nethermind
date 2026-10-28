// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.State;

// The assembly is Nethermind.State, because that is where StateNotRetainedException lives and Nethermind.Evm cannot
// reference it; the namespace is the one the extended interfaces live in, so callers need no extra using.
#pragma warning disable IDE0130
namespace Nethermind.Evm.State;
#pragma warning restore IDE0130

/// <summary>
/// Throwing counterparts of the <c>TryBeginScope</c> members, for callers to which an unavailable state is a broken
/// expectation rather than an ordinary answer.
/// </summary>
public static class WorldStateScopeExtensions
{
    /// <inheritdoc cref="IWorldState.TryBeginScope"/>
    /// <exception cref="StateNotRetainedException">The state at <paramref name="baseBlock"/> is unavailable.</exception>
    public static IDisposable BeginScope(this IWorldState worldState, BlockHeader? baseBlock) =>
        worldState.TryBeginScope(baseBlock, out IDisposable? scopeCloser) ? scopeCloser : ThrowUnavailable<IDisposable>(baseBlock);

    /// <inheritdoc cref="IWorldState.TryBeginScopeAtTarget"/>
    /// <exception cref="StateNotRetainedException">The parent header or its state is unavailable.</exception>
    public static IDisposable BeginScopeAtTarget(this IWorldState worldState, BlockHeader targetBlock) =>
        worldState.TryBeginScopeAtTarget(targetBlock, out IDisposable? scopeCloser) ? scopeCloser : ThrowNoParent<IDisposable>(targetBlock);

    /// <inheritdoc cref="IWorldStateScopeProvider.TryBeginScope"/>
    /// <exception cref="StateNotRetainedException">The state at <paramref name="baseBlock"/> is unavailable.</exception>
    public static IWorldStateScopeProvider.IScope BeginScope(this IWorldStateScopeProvider scopeProvider, BlockHeader? baseBlock, LocalMetrics metrics) =>
        scopeProvider.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? scope) ? scope : ThrowUnavailable<IWorldStateScopeProvider.IScope>(baseBlock);

    /// <inheritdoc cref="IWorldStateScopeProvider.TryBeginScopeAtTarget"/>
    /// <exception cref="StateNotRetainedException">The parent header or its state is unavailable.</exception>
    public static IWorldStateScopeProvider.IScope BeginScopeAtTarget(this IWorldStateScopeProvider scopeProvider, BlockHeader targetBlock, LocalMetrics metrics) =>
        scopeProvider.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? scope) ? scope : ThrowNoParent<IWorldStateScopeProvider.IScope>(targetBlock);

    /// <summary>Whether <paramref name="provider"/> holds the state <paramref name="targetBlock"/> executes on.</summary>
    /// <remarks>Advisory: the state is neither reserved nor pinned, so a later open can still refuse it.</remarks>
    public static bool HasRootForTarget(this IWorldStateScopeProvider provider, IStateHeaderProvider headers, BlockHeader targetBlock)
    {
        ArgumentNullException.ThrowIfNull(targetBlock);
        return headers.TryGetBaseBlock(targetBlock, out BlockHeader? baseBlock) && provider.HasRoot(baseBlock);
    }

    /// <summary>Opens <paramref name="provider"/> at the state <paramref name="targetBlock"/> executes on.</summary>
    /// <returns><c>false</c> when the parent header is unavailable, or when the provider refuses its state.</returns>
    public static bool TryBeginScopeAtBase(this IWorldStateScopeProvider provider, IStateHeaderProvider headers, BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        ArgumentNullException.ThrowIfNull(targetBlock);
        if (headers.TryGetBaseBlock(targetBlock, out BlockHeader? baseBlock)) return provider.TryBeginScope(baseBlock, metrics, out scope);

        scope = null;
        return false;
    }

    private static TScope ThrowUnavailable<TScope>(BlockHeader? baseBlock) =>
        throw new StateNotRetainedException($"State is unavailable for base block {baseBlock?.ToString(BlockHeader.Format.Short) ?? "pre-genesis"}.");

    private static TScope ThrowNoParent<TScope>(BlockHeader targetBlock) =>
        throw new StateNotRetainedException($"Parent state is unavailable for target block {targetBlock.ToString(BlockHeader.Format.Short)}.");
}
