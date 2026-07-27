// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BlockProfiler;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[NonParallelizable]
public class BlockProfilerPluginTests
{
    [TearDown]
    public void TearDown() => Environment.SetEnvironmentVariable("NETHERMIND_PROFILE_BLOCKS", null);

    [TestCase("", false)]
    [TestCase("not-a-number", false)]
    [TestCase("123", true)]
    [TestCase(" 123 , 456 ", true)]
    public void Enabled_follows_the_profile_blocks_variable(string value, bool expected)
    {
        Environment.SetEnvironmentVariable("NETHERMIND_PROFILE_BLOCKS", value);
        Assert.That(new BlockProfilerPlugin().Enabled, Is.EqualTo(expected));
    }
}
