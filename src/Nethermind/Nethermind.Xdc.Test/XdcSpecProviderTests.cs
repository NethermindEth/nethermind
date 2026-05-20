// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Xdc.Spec;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcSpecProviderTests
{
    [Test]
    public void V2Configs_ShouldThrow_IfMissingDefaultRoundZero()
    {
        List<V2ConfigParams> bad = new()
        {
            new() { SwitchRound = 2000 }
        };

        XdcChainSpecEngineParameters p = new();
        Action action = () => p.V2Configs = bad;

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void V2Configs_ShouldThrow_IfDuplicateSwitchRound()
    {
        List<V2ConfigParams> dup = new()
        {
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
            new() { SwitchRound = 2000 },
        };

        XdcChainSpecEngineParameters p = new();
        Action action = () => p.V2Configs = dup;

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void V2Configs_ShouldBeSortedBySwitchRound()
    {
        List<V2ConfigParams> unsorted = new()
        {
            new() { SwitchRound = 8000 },
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
        };

        XdcChainSpecEngineParameters p = new() { V2Configs = unsorted };

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
        List<V2ConfigParams> v2Configs = new()
        {
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
            new() { SwitchRound = 8000 },
            new() { SwitchRound = 220000 },
        };

        V2ConfigParams cfg = XdcReleaseSpec.GetConfigAtRound(v2Configs, round);

        cfg.SwitchRound.Should().Be(expectedSwitchRound);
    }

    [Test]
    public void GetXdcSpec_ReturnsDifferentSpecInstances()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                Eip1559Transition = 5,
            },
            EngineChainSpecParametersProvider = Substitute.For<IChainSpecParametersProvider>(),
        };
        XdcChainSpecEngineParameters parameters = new()
        {
            SwitchBlock = 1,
            V2Configs =
            {
                new() { SwitchRound = 0 },
                new() { SwitchRound = 10 },
            }
        };


        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, parameters, Substitute.For<ILogManager>());

        IXdcReleaseSpec specA = specProvider.GetXdcSpec(6, 6);
        specA.SwitchRound.Should().Be(0);

        IXdcReleaseSpec specB = specProvider.GetXdcSpec(11, 11);
        specB.SwitchRound.Should().Be(10);

        specA.SwitchRound.Should().Be(0);

        ReferenceEquals(specA, specB).Should().BeFalse();
    }
}
