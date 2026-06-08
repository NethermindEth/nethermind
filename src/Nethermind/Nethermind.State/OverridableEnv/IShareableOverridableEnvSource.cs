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
    /// <remarks>
    /// When <paramref name="blockOverride"/> is supplied it is applied to <paramref name="header"/> <b>in place</b>;
    /// see <see cref="IOverridableEnv.BuildAndOverride"/>. Callers must pass a header they own (e.g. a clone).
    /// </remarks>
    Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null);
}
