// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using Nethermind.Taiko.Rpc;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
public class TaikoEngineRpcModuleCompatibilityTests
{
    [Test]
    public void Public_api_should_preserve_legacy_constructor()
    {
        ConstructorInfo custodyConstructor = typeof(TaikoEngineRpcModule).GetConstructors()
            .Single(static constructor => constructor.GetParameters().Any(static parameter => parameter.ParameterType == typeof(IBlobCustodyTracker)));
        System.Type[] legacyParameters = custodyConstructor.GetParameters()
            .Where(static parameter => parameter.ParameterType != typeof(IBlobCustodyTracker))
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.That(typeof(TaikoEngineRpcModule).GetConstructor(legacyParameters), Is.Not.Null);
    }
}
