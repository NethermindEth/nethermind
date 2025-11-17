// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Linq;
using System.Reflection;
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
    public const ulong EcotoneTimestamp = 1_600;
    public const ulong HoloceneTimeStamp = 2_000;
    public const ulong IsthmusTimeStamp = 2_100;
    public const ulong JovianTimeStamp = 2_200;

    // Aggregates all fork timestamps and names
    public static readonly FrozenDictionary<ulong, string> ForkNameAt = typeof(Spec)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f =>
            f is { IsLiteral: true, IsInitOnly: false } &&
            f.FieldType == typeof(ulong) &&
            f.Name.EndsWith("timestamp", StringComparison.OrdinalIgnoreCase)
        ).ToFrozenDictionary(f => (ulong)f.GetRawConstantValue()!, f => f.Name[..^("timestamp".Length)]);

    public static readonly IOptimismSpecHelper Instance =
        new OptimismSpecHelper(new OptimismChainSpecEngineParameters
        {
            CanyonTimestamp = CanyonTimestamp,
            EcotoneTimestamp = EcotoneTimestamp,
            HoloceneTimestamp = HoloceneTimeStamp,
            IsthmusTimestamp = IsthmusTimeStamp,
            JovianTimestamp = JovianTimeStamp,
        });

    public static ISpecProvider BuildFor(params BlockHeader[] headers)
    {
        var specProvider = Substitute.For<ISpecProvider>();

        foreach (BlockHeader header in headers)
        {
            var spec = Substitute.For<ReleaseSpec>();

            spec.IsEip4844Enabled = true;
            spec.IsOpHoloceneEnabled = Instance.IsHolocene(header);
            spec.IsOpGraniteEnabled = Instance.IsGranite(header);
            spec.IsOpIsthmusEnabled = Instance.IsIsthmus(header);
            spec.IsOpJovianEnabled = Instance.IsJovian(header);

            specProvider.GetSpec(header).Returns(spec);
        }

        return specProvider;
    }
}
