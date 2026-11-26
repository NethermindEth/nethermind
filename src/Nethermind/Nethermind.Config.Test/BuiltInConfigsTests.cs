// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Nethermind.Config.Test;

public class BuiltInConfigsTests
{
    [Test]
    public void Names_are_sorted_and_have_no_extension()
    {
        Assert.That(BuiltInConfigs.Names, Does.Contain("mainnet"));
        Assert.That(BuiltInConfigs.Names, Has.None.EndsWith(".json"));
        Assert.That(BuiltInConfigs.Names, Is.Ordered.Using((IComparer)StringComparer.OrdinalIgnoreCase));
    }

    [TestCase("mainnet")]
    [TestCase("mainnet.json")]
    [TestCase("MainNet")]
    [TestCase("MAINNET.JSON")]
    public void Opens_by_name_with_or_without_extension_ignoring_case(string name)
    {
        using Stream? stream = BuiltInConfigs.Open(name);
        Assert.That(stream, Is.Not.Null);

        JsonConfigSource source = new(stream!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream!.CanRead, Is.True, "the caller owns the stream");
            Assert.That(source.GetRawValue("InitConfig", "ChainSpecPath").Value, Is.EqualTo("chainspec/foundation.json"));
        }
    }

    [TestCase("unknown")]
    [TestCase("configs/mainnet.json")]
    [TestCase("mainnet.cfg")]
    public void Returns_null_for_unknown_name(string name) => Assert.That(BuiltInConfigs.Open(name), Is.Null);

    [Test]
    public void Adds_configurations_a_plugin_embeds_without_replacing_built_in_ones()
    {
        IReadOnlyList<string> added = BuiltInConfigs.AddFrom(typeof(BuiltInConfigsTests).Assembly);
        IReadOnlyList<string> addedAgain = BuiltInConfigs.AddFrom(typeof(BuiltInConfigsTests).Assembly);
        using Stream? plugin = BuiltInConfigs.Open("plugin-test-network");
        using Stream? builtIn = BuiltInConfigs.Open("mainnet");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BuiltInConfigs.Names, Does.Contain("plugin-test-network"));
            Assert.That(added, Has.None.EqualTo("mainnet"), "a built-in name keeps its configuration");
            Assert.That(addedAgain, Is.Empty, "adding again changes nothing");
            Assert.That(new JsonConfigSource(plugin!).GetRawValue("InitConfig", "ChainSpecPath").Value, Is.EqualTo("chainspec/plugin-test-network.json"));
            Assert.That(new JsonConfigSource(builtIn!).GetRawValue("InitConfig", "ChainSpecPath").Value, Is.EqualTo("chainspec/foundation.json"));
        }
    }
}
