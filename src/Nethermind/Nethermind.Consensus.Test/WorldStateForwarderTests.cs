// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Consensus.Stateless;
using Nethermind.Evm.State;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class WorldStateForwarderTests
{
    private static readonly MethodInfo AdoptCode = typeof(IWorldState).GetMethod(nameof(IWorldState.AdoptCode))!;

    /// <summary>In-tree <see cref="IWorldState"/> implementations that wrap another world state.</summary>
    private static IEnumerable<TestCaseData> Forwarders() =>
        new[] { typeof(IWorldState).Assembly, typeof(WorldState).Assembly, typeof(StatelessExecutingWorldState).Assembly }
            .SelectMany(static a => a.GetTypes())
            .Where(static t => t.IsClass && typeof(IWorldState).IsAssignableFrom(t)
                && t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(static c => c.GetParameters().Any(static p => p.ParameterType == typeof(IWorldState))))
            .Select(static t => new TestCaseData(t).SetArgDisplayNames(t.Name));

    [Test]
    public void Forwarders_are_found() =>
        Assert.That(Forwarders().Select(static c => (Type)c.Arguments[0]!), Does.Contain(typeof(TracedAccessWorldState)).And.Contain(typeof(WorldStateDecorator)));

    /// <remarks>
    /// The interface default sends <see cref="IWorldState.AdoptCode"/> to the forwarder's own
    /// <see cref="IWorldState.InsertCode"/>, so a wrapped <see cref="TracedAccessWorldState"/> would record EIP-8298
    /// adopted code as bytecode and the generated block access list would not match the block's.
    /// </remarks>
    [TestCaseSource(nameof(Forwarders))]
    public void Forwarder_implements_AdoptCode_itself(Type forwarder)
    {
        InterfaceMapping map = forwarder.GetInterfaceMap(typeof(IWorldState));
        MethodInfo target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, AdoptCode)];

        Assert.That(target.DeclaringType, Is.Not.EqualTo(typeof(IWorldState)), $"{forwarder.Name} inherits the AdoptCode default");
    }
}
