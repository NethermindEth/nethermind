// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Core.Container;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcP2PCapabilityResolverTests
{
    [Test]
    public void Resolve_advertises_xdc_capabilities()
    {
        XdcP2PCapabilityResolver resolver = new();

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities, Is.EquivalentTo(new[]
        {
            new Capability(Protocol.Eth, 62),
            new Capability(Protocol.Eth, 63),
            new Capability(Protocol.Eth, 100),
        }));
    }

    [Test]
    public void Di_composition_drops_default_eth68_resolver()
    {
        // Mirrors NetworkModule (AddFirst default) followed by XdcModule (AddLast xdc, then deregister default).
        using IContainer container = new ContainerBuilder()
            .AddFirst<IP2PCapabilityResolver, DefaultP2PCapabilityResolver>()
            .AddLast<IP2PCapabilityResolver, XdcP2PCapabilityResolver>()
            .RemoveOrderedComponents<IP2PCapabilityResolver, DefaultP2PCapabilityResolver>()
            .Build();

        IP2PCapabilityResolver[] resolvers = container.Resolve<IP2PCapabilityResolver[]>();
        Assert.That(resolvers, Has.None.TypeOf<DefaultP2PCapabilityResolver>());

        HashSet<Capability> capabilities = [];
        foreach (IP2PCapabilityResolver resolver in resolvers) resolver.Resolve(capabilities);

        // eth/68 (the default's contribution) is gone; only XDC's versions remain.
        Assert.That(capabilities, Is.EquivalentTo(new[]
        {
            new Capability(Protocol.Eth, 62),
            new Capability(Protocol.Eth, 63),
            new Capability(Protocol.Eth, 100),
        }));
    }
}
