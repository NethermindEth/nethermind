// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class BuiltInPluginsTests
{
    [Test]
    public void EnsureAllBuiltInPluginsArePresent()
    {
        List<Type> pluginInAssembly = TypeDiscovery.FindNethermindBasedTypes(typeof(INethermindPlugin)).ToList();
        pluginInAssembly.Remove(typeof(IConsensusPlugin));

        HashSet<Type> builtInPlugins = NethermindPlugins.EmbeddedPlugins.ToHashSet();
        foreach (Type type in pluginInAssembly)
        {
            Assert.That(builtInPlugins, Does.Contain(type));
        }
    }
}
