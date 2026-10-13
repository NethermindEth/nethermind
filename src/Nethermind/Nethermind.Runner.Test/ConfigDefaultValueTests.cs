// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Api;
using Nethermind.Config.Test;
using Nethermind.Db;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[Parallelizable(ParallelScope.All)]
public class ConfigDefaultValueTests
{
    /// <remarks>
    /// <c>StandardConfigTests.ValidateDefaultValues</c> only loads <c>Nethermind.JsonRpc.dll</c>, so configs
    /// declared in other assemblies can drift from the defaults published in the docs and in <c>--help</c>.
    /// </remarks>
    [TestCaseSource(nameof(Properties))]
    public void Documented_default_matches_the_implementation(Type configInterface, Type implementation, string propertyName)
    {
        PropertyInfo property = configInterface.GetProperty(propertyName)!;
        StandardConfigTests.CheckDefault(property, Activator.CreateInstance(implementation));
    }

    private static IEnumerable<TestCaseData> Properties()
    {
        (Type Interface, Type Implementation)[] configs =
        [
            (typeof(INetworkConfig), typeof(NetworkConfig)),
            (typeof(IPruningConfig), typeof(PruningConfig)),
            (typeof(IInitConfig), typeof(InitConfig)),
        ];

        foreach ((Type configInterface, Type implementation) in configs)
        {
            foreach (PropertyInfo property in configInterface.GetProperties())
            {
                yield return new TestCaseData(configInterface, implementation, property.Name)
                    .SetName($"{configInterface.Name}.{property.Name}");
            }
        }
    }
}
