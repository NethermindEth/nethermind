// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// A utility that provide `IOverridableEnv<T>`
/// Don't forget to dispose it!
/// </summary>
/// <param name="overridableEnv"></param>
/// <param name="resolvedComponents"></param>
/// <typeparam name="T"></typeparam>
public class DisposableScopeOverridableEnv<T>(
    IOverridableEnv overridableEnv,
    T resolvedComponents
) : IOverridableEnv<T>
{
    public bool TryBuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, out Scope<T> scope)
    {
        if (!overridableEnv.TryBuildAndOverride(header, stateOverride, specOverride, out IDisposable? disposable))
        {
            scope = default!;
            return false;
        }

        scope = new Scope<T>(resolvedComponents, disposable);
        return true;
    }
}

/// <summary>
/// Reusable scope with one generic field called `Component`.
/// </summary>
/// <param name="component"></param>
/// <param name="scopeCloser"></param>
/// <typeparam name="T"></typeparam>
public class Scope<T>(T component, IDisposable scopeCloser) : IDisposable
{
    public T Component { get; } = component;

    public void Dispose() => scopeCloser.Dispose();
}
