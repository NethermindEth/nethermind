// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Config;
using Nethermind.Config.Test;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[Parallelizable(ParallelScope.All)]
public class ConfigDefaultValueTests
{
    /// <summary>
    /// Properties whose default depends on the machine or on process-wide state, so no literal
    /// <c>DefaultValue</c> can match it in every run.
    /// </summary>
    private static readonly HashSet<string> NonDeterministicDefaults =
    [
        "ILogIndexConfig.MaxReceiptsParallelism",     // Max(ProcessorCount / 2, 1)
        "ILogIndexConfig.MaxAggregationParallelism",  // Max(ProcessorCount / 2, 1)
        "ILogIndexConfig.MaxCompressionParallelism",  // Max(ProcessorCount / 2, 1)
        "ILogIndexConfig.CompressionAlgorithm",       // depends on the CPU instruction sets
        "IBlocksConfig.GasToken",                     // backed by the static GasTokenTicker, so any config that set it earlier (e.g. XDAI) wins
    ];

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
        where !NonDeterministicDefaults.Contains(name)
        select new TestCaseData(config.Interface, config.Implementation, property.Name).SetName(name);
}
