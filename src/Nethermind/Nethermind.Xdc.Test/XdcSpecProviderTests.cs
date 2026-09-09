// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Spec;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Nethermind.Int256;
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
    public void DynamicGasLimitBlock_takes_effect_on_its_own_block()
    {
        // The block gates block production, so it needs its own release spec boundary rather than rounding to
        // whichever transition happens to enclose it.
        const ulong dynamicGasLimitBlock = 1000;
        XdcChainSpecEngineParameters parameters = new()
        {
            SwitchBlock = 1,
            DynamicGasLimitBlock = dynamicGasLimitBlock,
            V2Configs = [new V2ConfigParams { SwitchRound = 0 }],
        };
        IChainSpecParametersProvider parametersProvider = Substitute.For<IChainSpecParametersProvider>();
        parametersProvider.AllChainSpecParameters.Returns([parameters]);
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { Eip1559Transition = 5 },
            EngineChainSpecParametersProvider = parametersProvider,
        };

        XdcChainSpecBasedSpecProvider specProvider = new(chainSpec, parameters, Substitute.For<ILogManager>());

        Assert.That(specProvider.GetXdcSpec(dynamicGasLimitBlock - 1).IsDynamicGasLimitBlock, Is.False);
        Assert.That(specProvider.GetXdcSpec(dynamicGasLimitBlock).IsDynamicGasLimitBlock, Is.True);
    }

    private static XdcChainSpecBasedSpecProvider BuildProvider(XdcChainSpecEngineParameters parameters)
    {
        parameters.V2Configs = [new V2ConfigParams { SwitchRound = 0 }];
        IChainSpecParametersProvider parametersProvider = Substitute.For<IChainSpecParametersProvider>();
        parametersProvider.AllChainSpecParameters.Returns([parameters]);
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { Eip1559Transition = 5 },
            EngineChainSpecParametersProvider = parametersProvider,
        };
        return new XdcChainSpecBasedSpecProvider(chainSpec, parameters, Substitute.For<ILogManager>());
    }

    private const ulong Raised = XdcConstants.DefaultMinGasPrice * XdcConstants.Gas50xMultiplier;

    [Test]
    public void MinimumGasPrice_is_raised_50x_on_Gas50xBlock()
    {
        const ulong gas50xBlock = 1000;
        XdcChainSpecBasedSpecProvider specProvider =
            BuildProvider(new XdcChainSpecEngineParameters { SwitchBlock = 1, Gas50xBlock = gas50xBlock });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(specProvider.GetXdcSpec(gas50xBlock - 1).MinimumGasPrice, Is.EqualTo((UInt256)XdcConstants.DefaultMinGasPrice));
            Assert.That(specProvider.GetXdcSpec(gas50xBlock).MinimumGasPrice, Is.EqualTo((UInt256)Raised));
        }
    }

    [TestCase(null, XdcConstants.DefaultMinGasPrice, TestName = "Unset uses the reference default")]
    [TestCase(1ul, XdcConstants.DefaultMinGasPrice, TestName = "Below the default is raised to it")]
    [TestCase(0ul, XdcConstants.DefaultMinGasPrice, TestName = "Zero is not gasless outside a subnet")]
    [TestCase(XdcConstants.DefaultMinGasPrice * 2, XdcConstants.DefaultMinGasPrice * 2, TestName = "Above the default is used")]
    public void MinimumGasPrice_follows_the_stated_floor(ulong? stated, ulong expectedBeforeGas50x)
    {
        XdcChainSpecBasedSpecProvider specProvider = BuildProvider(new XdcChainSpecEngineParameters
        {
            SwitchBlock = 1,
            Gas50xBlock = 1000,
            MinGasPrice = stated is null ? null : (UInt256)stated.Value,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(specProvider.GetXdcSpec(999).MinimumGasPrice, Is.EqualTo((UInt256)expectedBeforeGas50x));
            Assert.That(specProvider.GetXdcSpec(1000).MinimumGasPrice,
                Is.EqualTo((UInt256)expectedBeforeGas50x * XdcConstants.Gas50xMultiplier));
        }
    }

    [Test]
    public void MinimumGasPrice_on_a_subnet_is_raised_from_genesis_and_zero_means_gasless()
    {
        XdcChainSpecBasedSpecProvider raised =
            BuildProvider(new XdcSubnetChainSpecEngineParameters { SwitchBlock = 1 });
        XdcChainSpecBasedSpecProvider gasless =
            BuildProvider(new XdcSubnetChainSpecEngineParameters { SwitchBlock = 1, MinGasPrice = UInt256.Zero });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raised.GetXdcSpec(0).MinimumGasPrice, Is.EqualTo((UInt256)Raised), "no 50x transition on a subnet");
            Assert.That(raised.GetXdcSpec(1_000_000).MinimumGasPrice, Is.EqualTo((UInt256)Raised));
            Assert.That(gasless.GetXdcSpec(0).MinimumGasPrice, Is.EqualTo(UInt256.Zero), "gasless disables the check");
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
