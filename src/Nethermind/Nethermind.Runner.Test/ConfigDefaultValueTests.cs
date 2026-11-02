// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Config.Test;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Runner.Test;

[Parallelizable(ParallelScope.All)]
public class ConfigDefaultValueTests
{
    // TypeDiscovery caches on first use, so plugin assemblies must be loaded before the test cases are built,
    // or the cache misses plugin configs and breaks every later plugin load in this process
    static ConfigDefaultValueTests() =>
        new PluginLoader("plugins", new RealFileSystem(), NullLogger.Instance, NethermindPlugins.EmbeddedPlugins).Load();

    /// <remarks>
    /// <c>StandardConfigTests.ValidateDefaultValues</c> only loads <c>Nethermind.JsonRpc.dll</c>, so this checks every
    /// config the node discovers at startup, resolved the same way <see cref="ConfigProvider"/> resolves them.
    /// </remarks>
    [TestCaseSource(nameof(Properties))]
    public void Documented_default_matches_the_implementation(Type configInterface, Type implementation, string propertyName)
    {
        PropertyInfo property = configInterface.GetProperty(propertyName)!;
        StandardConfigTests.CheckDefault(property, Activator.CreateInstance(implementation));
    }

    [Test]
    public void Discovers_configs_from_all_assemblies()
    {
        string[] assemblies = ConfigTypes().Select(static c => c.Interface.Assembly.GetName().Name!).Distinct().ToArray();

        Assert.That(assemblies, Does.Contain("Nethermind.JsonRpc"));
        Assert.That(assemblies, Does.Contain("Nethermind.Network.Contract"));
        Assert.That(assemblies, Does.Contain("Nethermind.Merge.Plugin"));
    }

    private static IEnumerable<(Type Interface, Type Implementation)> ConfigTypes() =>
        TypeDiscovery.FindNethermindBasedTypes(typeof(IConfig))
            .Where(static t => t.IsInterface)
            .Select(static t => (Interface: t, Implementation: t.GetDirectInterfaceImplementation()))
            .OrderBy(static c => c.Interface.FullName, StringComparer.Ordinal);

    private static IEnumerable<TestCaseData> Properties() =>
        from config in ConfigTypes()
        from property in config.Interface.GetProperties()
        let name = $"{config.Interface.Name}.{property.Name}"
        where !StandardConfigTests.NonDeterministicDefaults.Contains(name)
        select new TestCaseData(config.Interface, config.Implementation, property.Name).SetName(name);
}
