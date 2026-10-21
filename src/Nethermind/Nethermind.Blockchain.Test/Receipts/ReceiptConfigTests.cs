// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Config.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

public class ReceiptConfigTests
{
    /// <remarks>
    /// <c>StandardConfigTests.ForEachProperty</c> globs only <c>Nethermind.JsonRpc.dll</c>, so a config
    /// declared in <c>Nethermind.Blockchain.dll</c> is free to drift from its documented defaults - which
    /// are what the docs publish and what an operator reads before overriding one.
    /// </remarks>
    [TestCaseSource(nameof(PropertyNames))]
    public void Documented_default_matches_the_implementation(string propertyName)
    {
        PropertyInfo property = typeof(IReceiptConfig).GetProperty(propertyName)!;
        ConfigItemAttribute? attribute = property.GetCustomAttribute<ConfigItemAttribute>();
        Assert.That(attribute, Is.Not.Null, "every config property is published, so every one needs a documented default");

        StandardConfigTests.CheckDefault(property, new ReceiptConfig());
    }

    private static IEnumerable<string> PropertyNames() => typeof(IReceiptConfig).GetProperties().Select(static p => p.Name);
}
