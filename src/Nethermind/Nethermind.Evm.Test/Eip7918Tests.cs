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
        (ulong parentExcessBlobGas, int parentBlobsCount, ulong parentBaseFee, ulong expectedExcessBlobGas) testCase,
        IReleaseSpec spec)
    {
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithBlobGasUsed(BlobGasCalculator.CalculateBlobGas(testCase.parentBlobsCount))
            .WithExcessBlobGas(testCase.parentExcessBlobGas)
            .WithBaseFee(testCase.parentBaseFee).TestObject;

        Assert.That(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec),
            Is.EqualTo(testCase.expectedExcessBlobGas));
    }

    private static IEnumerable<TestCaseData> GenerateTestCases()
    {
        IReleaseSpec[] specs = [Osaka.Instance];

        foreach (IReleaseSpec spec in specs)
        {
            foreach ((ulong parentExcessBlobGas, int parentBlobsCount, ulong parentBaseFee, ulong expectedExcessBlobGas) testCase in ExcessBlobGasTestCaseSource(spec))
            {
                yield return new TestCaseData(testCase, spec)
                    .SetName($"ExcessBlobGasTest_{spec.GetType().Name}_{testCase}");
            }
        }
    }

    private static IEnumerable<(ulong parentExcessBlobGas, int parentBlobsCount, ulong parentBaseFee, ulong expectedExcessBlobGas)>
        ExcessBlobGasTestCaseSource(IReleaseSpec spec)
    {
        yield return (spec.GetTargetBlobGasPerBlock() + 1, 0, 1_000_000_000, (spec.GetTargetBlobGasPerBlock() + 1) / 3);
        yield return (
            spec.GetTargetBlobGasPerBlock(),
            (int)spec.TargetBlobCount,
            1_000_000_000,
            2 * spec.GetTargetBlobGasPerBlock() / 3);
        yield return (spec.GetTargetBlobGasPerBlock() + 1, 0, 1, 1);
    }
}
