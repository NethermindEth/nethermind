// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

[TestFixture]
public class ForkScheduleSpecProviderTests
{
    public static ForkScheduleSpecProvider[] AllForkScheduleProviders =>
        [.. ChainSpecBasedSpecProvider.KnownProvidersByChainId.Values.OfType<ForkScheduleSpecProvider>()];

    [TestCaseSource(nameof(AllForkScheduleProviders))]
    public void Fork_schedule_is_in_ascending_activation_order(ForkScheduleSpecProvider provider)
    {
        ForkSpec[] schedule = provider.ForkSchedule;

        long previousBlock = long.MinValue;
        ulong? previousTimestamp = null;

        foreach (ForkSpec fork in schedule)
        {
            if (fork.Block is long block)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(previousTimestamp, Is.Null,
                        "block-keyed forks must come before any timestamp-keyed fork");
                    Assert.That(block, Is.GreaterThanOrEqualTo(previousBlock),
                        "block-keyed forks must be declared in ascending block order");
                }
                previousBlock = block;
            }
            else
            {
                Assert.That(fork.Timestamp, Is.Not.Null);
                if (previousTimestamp is ulong previous)
                    Assert.That(fork.Timestamp, Is.GreaterThanOrEqualTo(previous),
                        "timestamp-keyed forks must be declared in ascending timestamp order");
                previousTimestamp = fork.Timestamp;
            }
        }
    }

    public static IEnumerable<TestCaseData> ExpectedTransitionActivations
    {
        get
        {
            yield return new TestCaseData(MainnetSpecProvider.Instance, (ForkActivation[])
            [
                (ForkActivation)MainnetSpecProvider.HomesteadBlockNumber,
                (ForkActivation)MainnetSpecProvider.DaoForkBlockNumber,
                (ForkActivation)MainnetSpecProvider.TangerineWhistleBlockNumber,
                (ForkActivation)MainnetSpecProvider.SpuriousDragonBlockNumber,
                (ForkActivation)MainnetSpecProvider.ByzantiumBlockNumber,
                (ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber,
                (ForkActivation)MainnetSpecProvider.IstanbulBlockNumber,
                (ForkActivation)MainnetSpecProvider.MuirGlacierBlockNumber,
                (ForkActivation)MainnetSpecProvider.BerlinBlockNumber,
                (ForkActivation)MainnetSpecProvider.LondonBlockNumber,
                (ForkActivation)MainnetSpecProvider.ArrowGlacierBlockNumber,
                (ForkActivation)MainnetSpecProvider.GrayGlacierBlockNumber,
                MainnetSpecProvider.ShanghaiActivation,
                MainnetSpecProvider.CancunActivation,
                MainnetSpecProvider.PragueActivation,
                MainnetSpecProvider.OsakaActivation,
                MainnetSpecProvider.BPO1Activation,
                MainnetSpecProvider.BPO2Activation,
                MainnetSpecProvider.AmsterdamActivation,
                MainnetSpecProvider.BogotaActivation,
            ])
            { TestName = "Mainnet" };

            yield return new TestCaseData(ChiadoSpecProvider.Instance, (ForkActivation[])
            [
                (0, ChiadoSpecProvider.ShanghaiTimestamp),
                (0, ChiadoSpecProvider.CancunTimestamp),
                (0, ChiadoSpecProvider.PragueTimestamp),
                (0, ChiadoSpecProvider.OsakaTimestamp),
            ])
            { TestName = "Chiado" };

            yield return new TestCaseData(HoodiSpecProvider.Instance, (ForkActivation[])
            [
                (1, HoodiSpecProvider.ShanghaiTimestamp),
                (2, HoodiSpecProvider.CancunTimestamp),
                (3, HoodiSpecProvider.PragueTimestamp),
                (4, HoodiSpecProvider.OsakaTimestamp),
                (5, HoodiSpecProvider.BPO1Timestamp),
                (6, HoodiSpecProvider.BPO2Timestamp),
            ])
            { TestName = "Hoodi" };

            yield return new TestCaseData(SepoliaSpecProvider.Instance, (ForkActivation[])
            [
                (ForkActivation)SepoliaSpecProvider.MergeForkIdBlockNumber,
                (SepoliaSpecProvider.MergeForkIdBlockNumber, SepoliaSpecProvider.ShanghaiTimestamp),
                (SepoliaSpecProvider.MergeForkIdBlockNumber, SepoliaSpecProvider.CancunTimestamp),
                (SepoliaSpecProvider.MergeForkIdBlockNumber, SepoliaSpecProvider.PragueTimestamp),
                (SepoliaSpecProvider.MergeForkIdBlockNumber, SepoliaSpecProvider.OsakaTimestamp),
                (SepoliaSpecProvider.MergeForkIdBlockNumber, SepoliaSpecProvider.BPO1Timestamp),
                (SepoliaSpecProvider.MergeForkIdBlockNumber, SepoliaSpecProvider.BPO2Timestamp),
            ])
            { TestName = "Sepolia" };

            yield return new TestCaseData(MordenSpecProvider.Instance, (ForkActivation[])
            [
                (ForkActivation)MordenSpecProvider.HomesteadBlockNumber,
                (ForkActivation)MordenSpecProvider.SpuriousDragonBlockNumber,
            ])
            { TestName = "Morden" };
        }
    }

    [TestCaseSource(nameof(ExpectedTransitionActivations))]
    public void Transition_activations_match_expected(ForkScheduleSpecProvider provider, ForkActivation[] expected) =>
        Assert.That(provider.TransitionActivations, Is.EqualTo(expected));
}
