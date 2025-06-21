// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.OverridableEnv;

public class OverridableEnv<T>(
    IOverridableEnv overridableEnv,
    T resolvedComponents
) : IOverridableEnv<T>
{
    public IDisposable Build(Hash256 stateRoot, out T component)
    {
        IDisposable disposable = overridableEnv.Build(stateRoot);
        component = resolvedComponents;
        return disposable;
    }

    public IDisposable BuildAndOverride(BlockHeader header, out T component)
    {
        IDisposable disposable = overridableEnv.BuildAndOverride(header, null);
        component = resolvedComponents;
        return disposable;
    }

    public IDisposable BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride> stateOverride, out T component)
    {
        IDisposable disposable = overridableEnv.BuildAndOverride(header, stateOverride);
        component = resolvedComponents;
        return disposable;
    }
}
