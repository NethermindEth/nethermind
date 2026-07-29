// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        List<V2ConfigParams> bad =
        [
            new() { SwitchRound = 2000 }
        ];

        XdcChainSpecEngineParameters p = new();
        Action action = () => p.V2Configs = bad;

        Assert.That(action, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void V2Configs_ShouldThrow_IfDuplicateSwitchRound()
    {
        List<V2ConfigParams> dup =
        [
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
            new() { SwitchRound = 2000 },
        ];

        XdcChainSpecEngineParameters p = new();
        Action action = () => p.V2Configs = dup;

        Assert.That(action, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void V2Configs_ShouldBeSortedBySwitchRound()
    {
        List<V2ConfigParams> unsorted =
        [
            new() { SwitchRound = 8000 },
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
        ];

        XdcChainSpecEngineParameters p = new() { V2Configs = unsorted };

        Assert.That(p.V2Configs[0].SwitchRound, Is.EqualTo(0));
        Assert.That(p.V2Configs[1].SwitchRound, Is.EqualTo(2000));
        Assert.That(p.V2Configs[2].SwitchRound, Is.EqualTo(8000));
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
        List<V2ConfigParams> v2Configs =
        [
            new() { SwitchRound = 0 },
            new() { SwitchRound = 2000 },
            new() { SwitchRound = 8000 },
            new() { SwitchRound = 220000 },
        ];

        V2ConfigParams cfg = XdcReleaseSpec.GetConfigAtRound(v2Configs, round);

        Assert.That(cfg.SwitchRound, Is.EqualTo(expectedSwitchRound));
    }

    [Test]
    public void ApplyV2Config_AppliesNodeCaps()
    {
        XdcReleaseSpec spec = new()
        {
            V2Configs =
            [
                new()
                {
                    SwitchRound = 0,
                    MaxMasternodes = 10,
                    MaxProtectorNodes = 2,
                    MaxObserverNodes = 3,
                },
                new()
                {
                    SwitchRound = 10,
                    MaxMasternodes = 20,
                    MaxProtectorNodes = 4,
                    MaxObserverNodes = 5,
                },
            ],
        };

        spec.ApplyV2Config(10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.MaxMasternodes, Is.EqualTo(20));
            Assert.That(spec.MaxProtectorNodes, Is.EqualTo(4));
            Assert.That(spec.MaxObserverNodes, Is.EqualTo(5));
        }
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
        Assert.That(specA.SwitchRound, Is.EqualTo(0));

        IXdcReleaseSpec specB = specProvider.GetXdcSpec(11, 11);
        Assert.That(specB.SwitchRound, Is.EqualTo(10));

        Assert.That(specA.SwitchRound, Is.EqualTo(0));

        Assert.That(ReferenceEquals(specA, specB), Is.False);
    }
}
