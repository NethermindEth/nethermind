// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

/// <summary>
/// Contributes to the set of devp2p capabilities the node advertises in its P2P Hello message.
/// Implementations may add or remove capabilities based on node configuration and runtime state.
/// </summary>
/// <remarks>
/// The advertised set is computed once into a cached array and reused for every session, so <see cref="Resolve"/>
/// is not on the per-session hot path. Implementations whose contribution depends on mutable state must raise
/// <see cref="Changed"/> whenever it would change, so the cache is rebuilt. Static implementations never raise it.
/// </remarks>
public interface IP2PCapabilityResolver
{
    /// <summary>Adds and/or removes this resolver's capabilities from the running set.</summary>
    void Resolve(ISet<Capability> capabilities);

    /// <summary>Raised when this resolver's contribution changes, to invalidate the cached advertised set.</summary>
    event Action? Changed;
}
