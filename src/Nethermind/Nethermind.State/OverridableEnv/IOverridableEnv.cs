// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Autofac.Core;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// An <see cref="IOverridableEnv"/> is an environment where the world state and or the code repository can be overridden.
/// It is an <see cref="IModule"/> that can be used to configure an autofac child lifetime to provide the necessary components
/// that reflect the override. Any components within that lifetime should run between the build and dispose of the returned
/// disposable or there may be memory leak.
/// </summary>
public interface IOverridableEnv : IModule
{
    /// <remarks>
    /// When <paramref name="blockOverride"/> is supplied it is applied to <paramref name="header"/> <b>in place</b>
    /// (mutating Number/Timestamp/BaseFee/GasLimit/etc.), so the override is visible to the caller's block-execution
    /// context after this returns. The header is also assigned the post-state-override state root. Callers must pass a
    /// header they own (e.g. a clone), never a shared block-tree header.
    /// </remarks>
    IDisposable BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null, BlockOverride? blockOverride = null);

    /// <summary>
    /// Attempts to open the state required to execute <paramref name="targetBlock"/> (its parent state) and applies
    /// the overrides on top of it.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="BuildAndOverride"/> no header is mutated: the parent is the block tree's and a block override
    /// belongs to the caller's own target header.
    /// </remarks>
    /// <returns><c>false</c> when the parent header or its state is unavailable.</returns>
    bool TryBuildAndOverrideAtTarget(BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, [NotNullWhen(true)] out IDisposable? scope);
}

/// <summary>
/// I small wrapper around <see cref="IOverridableEnv"/> to help prevent accidentally using components that rely on
/// the overridden env outside of the env scope. Ideally, no other components from the child lifetime is extracted
/// aside from <see cref="IOverridableEnv{T}"/>. To use any components such as <see cref="ITransactionProcessor"/>, set the
/// <see cref="T"/> here to that component, then call one of the method here to get it.
/// Always dispose the scope when finished.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IOverridableEnv<T>
{
    /// <remarks>
    /// When <paramref name="blockOverride"/> is supplied it is applied to <paramref name="header"/> <b>in place</b>;
    /// see <see cref="IOverridableEnv.BuildAndOverride"/>. Callers must pass a header they own (e.g. a clone).
    /// </remarks>
    Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null, BlockOverride? blockOverride = null);

    /// <inheritdoc cref="IOverridableEnv.TryBuildAndOverrideAtTarget"/>
    bool TryBuildAndOverrideAtTarget(BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, [NotNullWhen(true)] out Scope<T>? scope);
}

public static class OverridableEnvExtensions
{
    /// <inheritdoc cref="IOverridableEnv.TryBuildAndOverrideAtTarget"/>
    /// <exception cref="InvalidOperationException">The parent header or its state is unavailable.</exception>
    public static IDisposable BuildAndOverrideAtTarget(this IOverridableEnv env, BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null) =>
        env.TryBuildAndOverrideAtTarget(targetBlock, stateOverride, specOverride, out IDisposable? scope) ? scope : ThrowUnavailable<IDisposable>(targetBlock);

    /// <inheritdoc cref="IOverridableEnv.TryBuildAndOverrideAtTarget"/>
    /// <exception cref="InvalidOperationException">The parent header or its state is unavailable.</exception>
    public static Scope<T> BuildAndOverrideAtTarget<T>(this IOverridableEnv<T> env, BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null) =>
        env.TryBuildAndOverrideAtTarget(targetBlock, stateOverride, specOverride, out Scope<T>? scope) ? scope : ThrowUnavailable<Scope<T>>(targetBlock);

    /// <inheritdoc cref="IOverridableEnv.TryBuildAndOverrideAtTarget"/>
    /// <exception cref="InvalidOperationException">The parent header or its state is unavailable.</exception>
    public static Scope<T> BuildAndOverrideAtTarget<T>(this IShareableOverridableEnvSource<T> source, BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride = null) =>
        source.TryBuildAndOverrideAtTarget(targetBlock, stateOverride, out Scope<T>? scope) ? scope : ThrowUnavailable<Scope<T>>(targetBlock);

    private static TScope ThrowUnavailable<TScope>(BlockHeader targetBlock) =>
        throw new InvalidOperationException($"Parent state is unavailable for target block {targetBlock.ToString(BlockHeader.Format.Short)}.");
}
