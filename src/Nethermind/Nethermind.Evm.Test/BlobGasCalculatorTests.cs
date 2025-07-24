// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class BlobGasCalculatorTests
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

    [TestCaseSource(nameof(BlobGasCostTestCaseSource))]
    public void Blob_base_fee_is_calculated_properly(
        (Transaction tx, ulong excessBlobGas, UInt256 expectedCost) testCase)
    {
        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(testCase.excessBlobGas).TestObject;

        bool success = BlobGasCalculator.TryCalculateBlobBaseFee(header, testCase.tx, Eip4844Constants.DefaultBlobGasPriceUpdateFraction, out UInt256 blobBaseFee);

        Assert.That(success, Is.True);
        Assert.That(blobBaseFee, Is.EqualTo(testCase.expectedCost));
    }

    [Test]
    public void Blob_base_fee_may_overflow()
    {
        var tx = Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(ulong.MaxValue).TestObject;

        bool success = BlobGasCalculator.TryCalculateBlobBaseFee(header, tx, Eip4844Constants.DefaultBlobGasPriceUpdateFraction, out UInt256 blobBaseFee);

        Assert.That(success, Is.False);
        Assert.That(blobBaseFee, Is.EqualTo(UInt256.MaxValue));
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

    public static IEnumerable<(Transaction tx, ulong excessBlobGas, UInt256 expectedCost)> BlobGasCostTestCaseSource()
    {
        yield return (Build.A.Transaction.TestObject, 0, 0);
        yield return (Build.A.Transaction.TestObject, 1000, 0);
        yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(0).TestObject, 1000, 0);
        yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1).TestObject, 0, 131072);
        yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1).TestObject, 10000000, 2490368);
        yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject, 0, 131072000);
        yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject, 10000000, 2490368000);
    }
}
