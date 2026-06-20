// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcP2PCapabilityResolverTests
{
    [Test]
    public void Resolve_replaces_default_eth68_with_xdc_capabilities()
    {
        XdcP2PCapabilityResolver resolver = new();

        HashSet<Capability> capabilities = [new(Protocol.Eth, 68)];
        resolver.Resolve(capabilities);

        Assert.That(capabilities, Is.EquivalentTo(new[]
        {
            new Capability(Protocol.Eth, 62),
            new Capability(Protocol.Eth, 63),
            new Capability(Protocol.Eth, 100),
        }));
    }
}
