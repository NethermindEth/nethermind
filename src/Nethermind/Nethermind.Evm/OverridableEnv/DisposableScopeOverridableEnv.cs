// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.OverridableEnv;

/// <summary>
/// A utility that provide `IOverridableEnv<T>`
/// Dont forget do dispose it!
/// </summary>
/// <param name="overridableEnv"></param>
/// <param name="resolvedComponents"></param>
/// <typeparam name="T"></typeparam>
public class DisposableScopeOverridableEnv<T>(
    IOverridableEnv overridableEnv,
    T resolvedComponents
) : IOverridableEnv<T>
{
    public Scope<T> BuildAndOverride(BlockHeader header)
    {
        IDisposable disposable = overridableEnv.BuildAndOverride(header, null);
        return new Scope<T>(resolvedComponents, disposable);
    }

    public Scope<T> BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride> stateOverride)
    {
        IDisposable disposable = overridableEnv.BuildAndOverride(header, stateOverride);
        return new Scope<T>(resolvedComponents, disposable);
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

    public void Dispose()
    {
        scopeCloser.Dispose();
    }
}
