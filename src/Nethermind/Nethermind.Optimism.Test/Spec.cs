// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Optimism.Test;

/// <summary>
/// Provides a shared instance of <see cref="IOptimismSpecHelper"/> over the fixed const parameters.
/// </summary>
public static class Spec
{
    public const ulong GenesisTimestamp = 1_000;
    public const ulong CanyonTimestamp = 1_300;
    public const ulong EcotoneTimestamp = 1_600;
    public const ulong GraniteTimestamp = 1_900;
    public const ulong HoloceneTimeStamp = 2_000;
    public const ulong IsthmusTimeStamp = 2_100;
    public const ulong JovianTimeStamp = 2_200;
    public const ulong KarstTimeStamp = 2_300;

    public static readonly IOptimismSpecHelper Instance =
        new OptimismSpecHelper(new OptimismChainSpecEngineParameters
        {
            CanyonTimestamp = CanyonTimestamp,
            EcotoneTimestamp = EcotoneTimestamp,
            GraniteTimestamp = GraniteTimestamp,
            HoloceneTimestamp = HoloceneTimeStamp,
            IsthmusTimestamp = IsthmusTimeStamp,
            JovianTimestamp = JovianTimeStamp,
            KarstTimestamp = KarstTimeStamp,
        });

    public static ISpecProvider BuildFor(params BlockHeader[] headers)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        foreach (BlockHeader header in headers)
        {
            OptimismReleaseSpec spec = Substitute.For<OptimismReleaseSpec>();

            spec.IsEip4844Enabled = true;
            spec.IsOpGraniteEnabled = Instance.IsGranite(header);
            spec.IsOpHoloceneEnabled = Instance.IsHolocene(header);
            spec.IsOpIsthmusEnabled = Instance.IsIsthmus(header);
            spec.IsOpJovianEnabled = Instance.IsJovian(header);
            spec.IsOpKarstEnabled = Instance.IsKarst(header);

            specProvider.GetSpec(header).Returns(spec);
        }

        return specProvider;
    }

    public static ISpecProvider BuildFor(params ulong[] timestamps)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        foreach (ulong timestamp in timestamps)
        {
            OptimismReleaseSpec spec = Substitute.For<OptimismReleaseSpec>();

            spec.IsEip4844Enabled = true;
            spec.IsOpGraniteEnabled = timestamp >= GraniteTimestamp;
            spec.IsOpHoloceneEnabled = timestamp >= HoloceneTimeStamp;
            spec.IsOpIsthmusEnabled = timestamp >= IsthmusTimeStamp;
            spec.IsOpJovianEnabled = timestamp >= JovianTimeStamp;
            spec.IsOpKarstEnabled = timestamp >= KarstTimeStamp;

            specProvider.GetSpec(Arg.Is<ForkActivation>(f => f.Timestamp == timestamp)).Returns(spec);
        }

        return specProvider;
    }
}
