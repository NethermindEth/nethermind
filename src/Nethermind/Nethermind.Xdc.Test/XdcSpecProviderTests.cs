// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Xdc.Spec;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class XdcSpecProviderTests
{
    [Test]
    public void V2Configs_ShouldThrow_IfMissingDefaultRoundZero()
    {
        var bad = new List<V2ConfigParams>
        {
            new() { SwitchRound = 2000 }
        };

        var p = new XdcChainSpecEngineParameters();
        Action action = () => p.V2Configs = bad;

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void V2Configs_ShouldThrow_IfDuplicateSwitchRound()
    {
        var dup = new List<V2ConfigParams>
        {
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
            new() { SwitchRound = 2000 },
        };

        var p = new XdcChainSpecEngineParameters();
        Action action = () => p.V2Configs = dup;

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void V2Configs_ShouldBeSortedBySwitchRound()
    {
        var unsorted = new List<V2ConfigParams>
        {
            new() { SwitchRound = 8000 },
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
        };

        var p = new XdcChainSpecEngineParameters { V2Configs = unsorted };

        p.V2Configs[0].SwitchRound.Should().Be(0);
        p.V2Configs[1].SwitchRound.Should().Be(2000);
        p.V2Configs[2].SwitchRound.Should().Be(8000);
    }

    [TestCase(0UL, 0UL)]
    [TestCase(1999UL, 0UL)]
    [TestCase(2000UL, 2000UL)]
    [TestCase(2001UL, 2000UL)]
    [TestCase(219999UL, 8000UL)]
    [TestCase(9_999_999UL, 220000UL)]
    public void ApplyV2Config_PicksExpectedConfigForRound(
        ulong round, ulong expectedSwitchRound)
    {
        var v2Configs = new List<V2ConfigParams>
        {
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
            new() { SwitchRound = 8000 },
            new() { SwitchRound = 220000 },
        };

        V2ConfigParams cfg = XdcReleaseSpec.GetConfigAtRound(v2Configs, round);

        cfg.SwitchRound.Should().Be(expectedSwitchRound);
    }
}
