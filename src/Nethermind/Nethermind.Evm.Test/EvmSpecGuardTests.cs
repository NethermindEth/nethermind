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
    private static readonly string[] s_flagNames =
    [
        nameof(IEvmSpec.ShiftOpcodesEnabled),
        nameof(IEvmSpec.CLZEnabled),
        nameof(IEvmSpec.ReturnDataOpcodesEnabled),
        nameof(IEvmSpec.ExtCodeHashOpcodeEnabled),
        nameof(IEvmSpec.ChainIdOpcodeEnabled),
        nameof(IEvmSpec.SelfBalanceOpcodeEnabled),
        nameof(IEvmSpec.BaseFeeEnabled),
        nameof(IEvmSpec.IsEip4844Enabled),
        nameof(IEvmSpec.IsEip7843Enabled),
        nameof(IEvmSpec.TransientStorageEnabled),
        nameof(IEvmSpec.MCopyIncluded),
        nameof(IEvmSpec.IncludePush0Instruction),
        nameof(IEvmSpec.IsEip8024Enabled),
        nameof(IEvmSpec.DelegateCallEnabled),
        nameof(IEvmSpec.Create2OpcodeEnabled),
        nameof(IEvmSpec.StaticCallEnabled),
        nameof(IEvmSpec.RevertOpcodeEnabled),
        nameof(IEvmSpec.UseNetGasMetering),
        nameof(IEvmSpec.UseNetGasMeteringWithAStipendFix),
        nameof(IEvmSpec.IsEip8037Enabled),
        nameof(IEvmSpec.IsEip7708Enabled),
    ];

    [Test]
    public void Cancun_compile_time_spec_matches_runtime_fork() => AssertMatches<CancunEvmSpec>(Cancun.Instance);

    [Test]
    public void Prague_compile_time_spec_matches_runtime_fork() => AssertMatches<PragueEvmSpec>(Prague.Instance);

    [Test]
    public void Osaka_compile_time_spec_matches_runtime_fork() => AssertMatches<OsakaEvmSpec>(Osaka.Instance);

    private static void AssertMatches<TSpec>(IReleaseSpec runtimeSpec) where TSpec : struct, IEvmSpec
    {
        int expected = EvmSpecFingerprint.Compute(runtimeSpec);
        int actual = EvmSpecFingerprint.Compute<TSpec>();
        if (expected == actual)
            return;

        int diff = expected ^ actual;
        List<string> wrong = [];
        for (int i = 0; i < s_flagNames.Length; i++)
        {
            if ((diff & (1 << i)) != 0)
                wrong.Add(s_flagNames[i]);
        }

        Assert.Fail($"{typeof(TSpec).Name} diverges from the runtime fork on: {string.Join(", ", wrong)} " +
                    "— the compile-time struct must be an exact copy of the runtime spec flags.");
    }
}
