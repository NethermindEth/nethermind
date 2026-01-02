// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Ethash.Test;

public class ChainSpecTest
{
    [Test]
    public void Bound_divisors_set_correctly()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { GasLimitBoundDivisor = 17 }
        };

        chainSpec.EngineChainSpecParametersProvider =
            new TestChainSpecParametersProvider(new EthashChainSpecEngineParameters { DifficultyBoundDivisor = 19 });


        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GenesisSpec.DifficultyBoundDivisor, Is.EqualTo(19));
        Assert.That(provider.GenesisSpec.GasLimitBoundDivisor, Is.EqualTo(17));
    }

    [Test]
    public void Difficulty_bomb_delays_loaded_correctly()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
        };
        chainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
            new EthashChainSpecEngineParameters
            {
                DifficultyBombDelays = new Dictionary<long, long>
                {
                    { 3, 100 },
                    { 7, 200 },
                    { 13, 300 },
                    { 17, 400 },
                    { 19, 500 },
                }
            });

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GetSpec((ForkActivation)3).DifficultyBombDelay, Is.EqualTo(100));
        Assert.That(provider.GetSpec((ForkActivation)7).DifficultyBombDelay, Is.EqualTo(300));
        Assert.That(provider.GetSpec((ForkActivation)13).DifficultyBombDelay, Is.EqualTo(600));
        Assert.That(provider.GetSpec((ForkActivation)17).DifficultyBombDelay, Is.EqualTo(1000));
        Assert.That(provider.GetSpec((ForkActivation)19).DifficultyBombDelay, Is.EqualTo(1500));
    }

    [Test]
    public void Eip_transitions_loaded_correctly()
    {
        const long maxCodeTransition = 1;
        const long maxCodeSize = 1;

        ChainSpec chainSpec = new()
        {
            ByzantiumBlockNumber = 1960,
            ConstantinopleBlockNumber = 6490,
            Parameters = new ChainParameters
            {
                MaxCodeSizeTransition = maxCodeTransition,
                MaxCodeSize = maxCodeSize,
                Registrar = Address.Zero,
                MinGasLimit = 11,
                MinHistoryRetentionEpochs = 11,
                GasLimitBoundDivisor = 13,
                MaximumExtraDataSize = 17,
                Eip140Transition = 1400L,
                Eip145Transition = 1450L,
                Eip150Transition = 1500L,
                Eip152Transition = 1520L,
                Eip155Transition = 1550L,
                Eip160Transition = 1600L,
                Eip161abcTransition = 1580L,
                Eip161dTransition = 1580L,
                Eip211Transition = 2110L,
                Eip214Transition = 2140L,
                Eip658Transition = 6580L,
                Eip1014Transition = 10140L,
                Eip1052Transition = 10520L,
                Eip1108Transition = 11080L,
                Eip1283Transition = 12830L,
                Eip1283DisableTransition = 12831L,
                Eip1344Transition = 13440L,
                Eip1884Transition = 18840L,
                Eip2028Transition = 20280L,
                Eip2200Transition = 22000L,
                Eip2315Transition = 23150L,
                Eip2565Transition = 25650L,
                Eip2929Transition = 29290L,
                Eip2930Transition = 29300L,
                Eip1559Transition = 15590L,
                Eip1559FeeCollectorTransition = 15591L,
                FeeCollector = Address.SystemUser,
                Eip1559BaseFeeMinValueTransition = 15592L,
                Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue,
                Eip3198Transition = 31980L,
                Eip3529Transition = 35290L,
                Eip3541Transition = 35410L,
                Eip1283ReenableTransition = 23000L,
                ValidateChainIdTransition = 24000L,
                ValidateReceiptsTransition = 24000L,
                MergeForkIdTransition = 40000L,
                Eip3651TransitionTimestamp = 1000000012,
                Eip3855TransitionTimestamp = 1000000012,
                Eip3860TransitionTimestamp = 1000000012,
                Eip1153TransitionTimestamp = 1000000024,
                Eip2537TransitionTimestamp = 1000000024,

                Eip7702TransitionTimestamp = 1000000032,
            }
        };
        chainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
            new EthashChainSpecEngineParameters
            {
                HomesteadTransition = 70,
                Eip100bTransition = 1000
            });


        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition - 1)).MaxCodeSize, Is.EqualTo(long.MaxValue), "one before");
        Assert.That(provider.GetSpec((ForkActivation)maxCodeTransition).MaxCodeSize, Is.EqualTo(maxCodeSize), "at transition");
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition + 1)).MaxCodeSize, Is.EqualTo(maxCodeSize), "one after");

        ReleaseSpec expected = new();

        void TestTransitions(ForkActivation activation, Action<ReleaseSpec> changes)
        {
            changes(expected);
            IReleaseSpec underTest = provider.GetSpec(activation);
            underTest.Should().BeEquivalentTo(expected);
        }

        TestTransitions((ForkActivation)0L, r =>
        {
            r.DifficultyBoundDivisor = 0x800;
            r.MinGasLimit = 11L;
            r.MinHistoryRetentionEpochs = 11L;
            r.GasLimitBoundDivisor = 13L;
            r.MaximumExtraDataSize = 17L;
            r.MaxCodeSize = long.MaxValue;
            r.Eip1559TransitionBlock = 15590L;
            r.IsTimeAdjustmentPostOlympic = true;
            r.MaximumUncleCount = 2;
            r.WithdrawalTimestamp = ulong.MaxValue;
            r.Eip4844TransitionTimestamp = ulong.MaxValue;
        });

        TestTransitions((ForkActivation)1L, r =>
        {
            r.MaxCodeSize = maxCodeSize;
            r.IsEip170Enabled = true;
        });
        TestTransitions((ForkActivation)70L, r => { r.IsEip2Enabled = r.IsEip7Enabled = true; });
        TestTransitions((ForkActivation)1000L, r => { r.IsEip100Enabled = true; });
        TestTransitions((ForkActivation)1400L, r => { r.IsEip140Enabled = true; });
        TestTransitions((ForkActivation)1450L, r => { r.IsEip145Enabled = true; });
        TestTransitions((ForkActivation)1500L, r => { r.IsEip150Enabled = true; });
        TestTransitions((ForkActivation)1520L, r => { r.IsEip152Enabled = true; });
        TestTransitions((ForkActivation)1550L, r => { r.IsEip155Enabled = true; });
        TestTransitions((ForkActivation)1580L, r => { r.IsEip158Enabled = true; });
        TestTransitions((ForkActivation)1600L, r => { r.IsEip160Enabled = true; });
        TestTransitions((ForkActivation)1960L,
            r => { r.IsEip196Enabled = r.IsEip197Enabled = r.IsEip198Enabled = r.IsEip649Enabled = true; });
        TestTransitions((ForkActivation)2110L, r => { r.IsEip211Enabled = true; });
        TestTransitions((ForkActivation)2140L, r => { r.IsEip214Enabled = true; });
        TestTransitions((ForkActivation)6580L, r => { r.IsEip658Enabled = r.IsEip1234Enabled = true; });
        TestTransitions((ForkActivation)10140L, r => { r.IsEip1014Enabled = true; });
        TestTransitions((ForkActivation)10520L, r => { r.IsEip1052Enabled = true; });
        TestTransitions((ForkActivation)11180L, r => { r.IsEip1108Enabled = true; });
        TestTransitions((ForkActivation)12830L, r => { r.IsEip1283Enabled = true; });
        TestTransitions((ForkActivation)12831L, r => { r.IsEip1283Enabled = false; });
        TestTransitions((ForkActivation)13440L, r => { r.IsEip1344Enabled = true; });
        TestTransitions((ForkActivation)15590L, r => { r.IsEip1559Enabled = true; });
        TestTransitions((ForkActivation)15591L, r => { r.FeeCollector = Address.SystemUser; });
        TestTransitions((ForkActivation)15592L, r => { r.Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue; });
        TestTransitions((ForkActivation)18840L, r => { r.IsEip1884Enabled = true; });
        TestTransitions((ForkActivation)20280L, r => { r.IsEip2028Enabled = true; });
        TestTransitions((ForkActivation)22000L, r => { r.IsEip2200Enabled = true; });
        TestTransitions((ForkActivation)23000L, r => { r.IsEip1283Enabled = r.IsEip1344Enabled = true; });
        TestTransitions((ForkActivation)24000L, r => { r.ValidateChainId = r.ValidateReceipts = true; });
        TestTransitions((ForkActivation)29290L, r => { r.IsEip2929Enabled = r.IsEip2565Enabled = true; });
        TestTransitions((ForkActivation)29300L, r => { r.IsEip2930Enabled = true; });
        TestTransitions((ForkActivation)31980L, r => { r.IsEip3198Enabled = true; });
        TestTransitions((ForkActivation)35290L, r => { r.IsEip3529Enabled = true; });
        TestTransitions((ForkActivation)35410L, r => { r.IsEip3541Enabled = true; });
        TestTransitions((ForkActivation)35410L, r => { r.IsEip3541Enabled = true; });


        TestTransitions((41000L, 1000000012), r =>
        {
            r.IsEip3651Enabled = true;
            r.IsEip3855Enabled = true;
            r.IsEip3860Enabled = true;
        });
        TestTransitions((40001L, 1000000024), r => { r.IsEip1153Enabled = r.IsEip2537Enabled = true; });
        TestTransitions((40001L, 1000000032), r => { r.IsEip7702Enabled = true; });
    }

}
