// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Test-only adapters around the production <c>TryBeginScope</c>/<c>TryBuild</c>/<c>TryBuildAndOverride</c>
/// APIs. Production code must use the explicit <c>Try*</c> form so the "no state" outcome is handled at the
/// call site; tests typically arrange a known-present state and just want a throwing convenience wrapper.
/// </summary>
public static class ScopeProviderTestExtensions
{
    public static IDisposable BeginScope(this IWorldState worldState, BlockHeader? baseBlock)
    {
        if (!worldState.TryBeginScope(baseBlock, out IDisposable? closer))
        {
            throw new StateUnavailableException(baseBlock);
        }
        return closer;
    }

    public static IWorldStateScopeProvider.IScope BeginScope(this IWorldStateScopeProvider provider, BlockHeader? baseBlock)
    {
        if (!provider.TryBeginScope(baseBlock, out IWorldStateScopeProvider.IScope? scope))
        {
            throw new StateUnavailableException(baseBlock);
        }
        return scope;
    }

    public static IReadOnlyTxProcessingScope Build(this IReadOnlyTxProcessorSource source, BlockHeader? baseBlock)
    {
        if (!source.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? scope))
        {
            throw new StateUnavailableException(baseBlock);
        }
        return scope;
    }

    public static IReadOnlyTxProcessingScope Build(this IShareableTxProcessorSource source, BlockHeader? baseBlock)
    {
        if (!source.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? scope))
        {
            throw new StateUnavailableException(baseBlock);
        }
        return scope;
    }

    public static IDisposable BuildAndOverride(
        this IOverridableEnv env,
        BlockHeader? header,
        Dictionary<Address, AccountOverride>? stateOverride = null,
        IReleaseSpec? specOverride = null)
    {
        if (!env.TryBuildAndOverride(header, stateOverride, specOverride, out IDisposable? closer))
        {
            throw new StateUnavailableException(header);
        }
        return closer;
    }

    public static Scope<T> BuildAndOverride<T>(
        this IOverridableEnv<T> env,
        BlockHeader? header,
        Dictionary<Address, AccountOverride>? stateOverride = null,
        IReleaseSpec? specOverride = null)
    {
        if (!env.TryBuildAndOverride(header, stateOverride, specOverride, out Scope<T> scope))
        {
            throw new StateUnavailableException(header);
        }
        return scope;
    }

    public static Scope<T> BuildAndOverride<T>(
        this IShareableOverridableEnvSource<T> source,
        BlockHeader? header,
        Dictionary<Address, AccountOverride>? stateOverride = null)
    {
        if (!source.TryBuildAndOverride(header, stateOverride, out Scope<T> scope))
        {
            throw new StateUnavailableException(header);
        }
        return scope;
    }
}
