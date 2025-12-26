// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class BuiltInPluginsTests
{
    [Test]
    public void EnsureAllBuiltInPluginsArePresent()
    {
        var pluginInAssembly = TypeDiscovery.FindNethermindBasedTypes(typeof(INethermindPlugin)).ToList();
        pluginInAssembly.Remove(typeof(IConsensusPlugin));
        pluginInAssembly.Remove(typeof(IConsensusWrapperPlugin));

        var builtInPlugins = NethermindPlugins.EmbeddedPlugins.ToHashSet();
        foreach (Type type in pluginInAssembly)
        {
            builtInPlugins.Should().Contain(type);
        }
    }
}
