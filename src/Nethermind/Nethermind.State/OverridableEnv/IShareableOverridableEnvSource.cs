// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Thread-safe source of <see cref="IOverridableEnv{T}"/> scopes. Callers hold a
/// <see cref="Scope{T}"/> for the request lifetime; the env is recycled on dispose. Concurrent
/// callers each get an independent env, so there is no shared mutable state.
/// </summary>
public interface IShareableOverridableEnvSource<T> : IDisposable
{
    /// <summary>
    /// Attempt to build a typed scope; returns <c>false</c> when the underlying state is unavailable.
    /// </summary>
    bool TryBuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride, out Scope<T> scope);
}
