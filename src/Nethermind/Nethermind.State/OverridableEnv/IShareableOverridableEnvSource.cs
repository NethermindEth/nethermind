// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Thread-safe source of <see cref="IOverridableEnv{T}"/> scopes. Callers obtain a
/// <see cref="Scope{T}"/> for the duration of a request; the underlying env is recycled when the
/// scope is disposed. Multiple callers can hold scopes concurrently — each backed by an independent
/// env so there is no shared mutable state.
/// </summary>
/// <remarks>
/// Mirrors the role of <c>IShareableTxProcessorSource</c> on the read-only side: a separate
/// abstraction that <i>manages</i> envs rather than pretending to <i>be</i> one. The underlying
/// <see cref="IOverridableEnv{T}"/> is intentionally single-call (its <c>_worldScopeCloser</c>
/// throws on reentry), so wrapping a pool behind the same interface would be misleading.
/// </remarks>
public interface IShareableOverridableEnvSource<T> : IDisposable
{
    Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null);
}
