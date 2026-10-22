// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;
using NUnit.Framework;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

[TestFixture]
public class PatternAnalyzerConfigTests
{
    [Test]
    public void GetIgnoreSet_ReturnsEmpty_WhenIgnoreIsDefault()
    {
        PatternAnalyzerConfig config = new();
        Assert.That(config.Ignore, Is.EqualTo(string.Empty));
        Assert.DoesNotThrow(() => config.GetIgnoreSet());
        Assert.That(config.GetIgnoreSet(), Is.Empty);
    }

    [Test]
    public void GetIgnoreSet_ReturnsEmpty_WhenIgnoreIsWhitespace()
    {
        PatternAnalyzerConfig config = new() { Ignore = "   " };
        Assert.That(config.GetIgnoreSet(), Is.Empty);
    }

    [Test]
    public void GetIgnoreSet_ParsesAndTrimsCommaSeparatedInstructions()
    {
        PatternAnalyzerConfig config = new() { Ignore = "JUMPDEST , JUMP" };
        HashSet<Instruction> set = config.GetIgnoreSet();
        Assert.That(set, Is.EquivalentTo(new[] { Instruction.JUMPDEST, Instruction.JUMP }));
    }
}
