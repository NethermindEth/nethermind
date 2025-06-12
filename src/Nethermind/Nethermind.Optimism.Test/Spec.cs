// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.Optimism.Test;

/// <summary>
/// Provides a shared instance of <see cref="IOptimismSpecHelper"/> over the fixed const parameters.
/// </summary>
public static class Spec
{
    public const ulong GenesisTimestamp = 1_000;
    public const ulong CanyonTimestamp = 1_300;
    public const ulong HoloceneTimeStamp = 2_000;
    public const ulong IsthmusTimeStamp = 2_100;

    public static readonly IOptimismSpecHelper Instance =
        new OptimismSpecHelper(new OptimismChainSpecEngineParameters
        {
            CanyonTimestamp = CanyonTimestamp,
            HoloceneTimestamp = HoloceneTimeStamp,
            IsthmusTimestamp = IsthmusTimeStamp
        });

    public static ISpecProvider BuildFor(BlockHeader header)
    {
        var spec = Substitute.For<ReleaseSpec>();

        spec.IsOpHoloceneEnabled = Instance.IsHolocene(header);
        spec.IsOpGraniteEnabled = Instance.IsGranite(header);
        spec.IsOpIsthmusEnabled = Instance.IsIsthmus(header);

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(spec);
        return specProvider;
    }
}
