// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

/// <summary>
/// Contributes the capabilities every node advertises by default (currently eth/68).
/// </summary>
/// <remarks>
/// Registered first so that chain-specific resolvers (e.g. XDC) can remove or build on the default set.
/// </remarks>
public class DefaultP2PCapabilityResolver : IP2PCapabilityResolver
{
    // The default set is static, so the cache never needs invalidating.
    public event Action? Changed { add { } remove { } }

    public void Resolve(ISet<Capability> capabilities) => capabilities.Add(new Capability(Protocol.Eth, 68));
}
