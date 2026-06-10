// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Locks every compile-time <see cref="IEvmSpec"/> struct to its runtime fork instance, flag by
/// flag. The whole safety story of the specialized dispatch rests on these structs being exact
/// copies of the runtime spec — a divergence here is a consensus bug caught as a unit test.
/// </summary>
[TestFixture]
public class EvmSpecGuardTests
{
    [Test]
    public void CancunEvmSpec_ComparedToRuntimeFork_MatchesFlagByFlag() => AssertMatches<CancunEvmSpec>(Cancun.Instance);

    [Test]
    public void PragueEvmSpec_ComparedToRuntimeFork_MatchesFlagByFlag() => AssertMatches<PragueEvmSpec>(Prague.Instance);

    [Test]
    public void OsakaEvmSpec_ComparedToRuntimeFork_MatchesFlagByFlag() => AssertMatches<OsakaEvmSpec>(Osaka.Instance);

    /// <summary>
    /// THE engagement guarantee for the specialized dispatch: the spec the MAINNET provider
    /// serves at the chain tip must fingerprint-match one of the specialized structs —
    /// otherwise every mainnet frame silently takes the generic table path and the
    /// specialization is dead weight (the exact failure mode that cost deploy cycles before).
    /// </summary>
    [Test]
    public void MainnetTipSpec_WhenFingerprinted_SelectsASpecializedDispatch()
    {
        IReleaseSpec tipSpec = Specs.MainnetSpecProvider.Instance.GetSpec(
            new Core.Specs.ForkActivation(long.MaxValue / 2, ulong.MaxValue / 2));
        int tipFingerprint = EvmSpecFingerprint.Compute(tipSpec);

        Assert.That(
            tipFingerprint == EvmSpecFingerprint.Compute<OsakaEvmSpec>()
            || tipFingerprint == EvmSpecFingerprint.Compute<CancunEvmSpec>(),
            $"the mainnet tip spec ({tipSpec.Name}) matches NO specialized dispatch struct — " +
            "mainnet would silently run the generic table path");
    }

    private static void AssertMatches<TSpec>(IReleaseSpec runtimeSpec) where TSpec : struct, IEvmSpec
    {
        int expected = EvmSpecFingerprint.Compute(runtimeSpec);
        int actual = EvmSpecFingerprint.Compute<TSpec>();
        if (expected == actual)
            return;

        int diff = expected ^ actual;
        List<string> wrong = [];
        for (int i = 0; i < EvmSpecFingerprint.FlagNames.Length; i++)
        {
            if ((diff & (1 << i)) != 0)
                wrong.Add(EvmSpecFingerprint.FlagNames[i]);
        }

        Assert.Fail($"{typeof(TSpec).Name} diverges from the runtime fork on: {string.Join(", ", wrong)} " +
                    "— the compile-time struct must be an exact copy of the runtime spec flags.");
    }
}
