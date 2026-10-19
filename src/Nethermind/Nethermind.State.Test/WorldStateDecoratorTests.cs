// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using Nethermind.Evm.State;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class WorldStateDecoratorTests
{
    /// <summary>
    /// A prefetch member the decorator leaves to its interface default answers for the decorator instead of the
    /// decorated state, which silently turns prefetch off for every decorated world state.
    /// </summary>
    [TestCase(nameof(IWorldState.Prefetch))]
    [TestCase("get_" + nameof(IWorldState.SupportsPrefetch))]
    public void Decorator_forwards_prefetch_members(string member)
    {
        InterfaceMapping map = typeof(WorldStateDecorator).GetInterfaceMap(typeof(IWorldState));
        MethodInfo target = map.TargetMethods.Single(method => method.Name == member);

        Assert.That(target.DeclaringType, Is.EqualTo(typeof(WorldStateDecorator)));
    }

    [Test]
    public void Decorator_reports_the_prefetch_support_of_the_decorated_state([Values] bool supported)
    {
        IWorldState inner = Substitute.For<IWorldState>();
        inner.SupportsPrefetch.Returns(supported);

        Assert.That(new Forwarding(inner).SupportsPrefetch, Is.EqualTo(supported));
    }

    private sealed class Forwarding(IWorldState state) : WorldStateDecorator(state);
}
