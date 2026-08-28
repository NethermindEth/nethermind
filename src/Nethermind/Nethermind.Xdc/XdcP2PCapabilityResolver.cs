// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Xdc.P2P;

namespace Nethermind.Xdc;

/// <summary>
/// XDC advertises only the versions <c>XdcModule</c> registers a handler for. The default eth/68 resolver is
/// dropped at registration (see <c>XdcModule</c>), so this resolver contributes the whole set.
/// </summary>
public class XdcP2PCapabilityResolver : IP2PCapabilityResolver
{
    // XDC's capability set is static, so the cache never needs invalidating.
    public event Action? Changed { add { } remove { } }

    public void Resolve(ISet<Capability> capabilities)
    {
        capabilities.Add(new Capability(Protocol.Eth, XdcProtocolVersions.Legacy));
        capabilities.Add(new Capability(Protocol.Eth, XdcProtocolVersions.Xdc164));
        capabilities.Add(new Capability(Protocol.Eth, XdcProtocolVersions.Xdc165));
    }
}
