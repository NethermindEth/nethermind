// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7918Tests : VirtualMachineTestsBase
{
    [Test]
    [TestCaseSource(nameof(GenerateTestCases))]
    public void Excess_blob_gas_is_calculated_properly(
        (ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas) testCase,
        IReleaseSpec spec,
        bool areBlobsEnabled)
    {
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithBlobGasUsed(BlobGasCalculator.CalculateBlobGas(testCase.parentBlobsCount))
            .WithExcessBlobGas(testCase.parentExcessBlobGas).TestObject;

        Assert.That(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec),
            Is.EqualTo(areBlobsEnabled ? testCase.expectedExcessBlobGas : null));
    }

    private static IEnumerable<TestCaseData> GenerateTestCases()
    {
        (IReleaseSpec Instance, bool)[] specs =
        [
            (Homestead.Instance, false),
            (Frontier.Instance, false),
            (SpuriousDragon.Instance, false),
            (TangerineWhistle.Instance, false),
            (Byzantium.Instance, false),
            (Constantinople.Instance, false),
            (ConstantinopleFix.Instance, false),
            (Istanbul.Instance, false),
            (MuirGlacier.Instance, false),
            (Berlin.Instance, false),
            (GrayGlacier.Instance, false),
            (Shanghai.Instance, false),
            (Cancun.Instance, true),
            (Prague.Instance, true)
        ];

        foreach ((IReleaseSpec spec, bool areBlobsEnabled) in specs)
        {
            foreach ((ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas) testCase in ExcessBlobGasTestCaseSource(spec))
            {
                yield return new TestCaseData(testCase, spec, areBlobsEnabled)
                    .SetName($"ExcessBlobGasTest_{spec.GetType().Name}_{testCase}");
            }
        }
    }

    private static IEnumerable<(ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas)>
        ExcessBlobGasTestCaseSource(IReleaseSpec spec)
    {
        yield return (0, 0, 0);
        yield return (0, (int)spec.TargetBlobCount - 1, 0);
        yield return (0, (int)spec.TargetBlobCount, 0);
        yield return (100000, (int)spec.TargetBlobCount, 100000);
        yield return (0, (int)spec.TargetBlobCount + 1, Eip4844Constants.GasPerBlob * 1);
        yield return (spec.GetTargetBlobGasPerBlock(), 1, Eip4844Constants.GasPerBlob * 1);
        yield return (spec.GetTargetBlobGasPerBlock(), 0, 0);
        yield return (spec.GetTargetBlobGasPerBlock(), 2, Eip4844Constants.GasPerBlob * 2);
        yield return (spec.GetMaxBlobGasPerBlock(), 1, (spec.MaxBlobCount + 1 - spec.TargetBlobCount) * Eip4844Constants.GasPerBlob);
        yield return (1, (int)spec.TargetBlobCount, 1);
        yield return (
            spec.GetMaxBlobGasPerBlock(),
            (int)spec.MaxBlobCount,
            (spec.MaxBlobCount * 2 - spec.TargetBlobCount) * Eip4844Constants.GasPerBlob
        );
    }

}
