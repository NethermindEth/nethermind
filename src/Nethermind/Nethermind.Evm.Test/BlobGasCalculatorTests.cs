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
    private void TestExcessBlobGas(IReleaseSpec spec, bool areBlobsEnabled, (ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas, ulong? targetBlobCount) testCase)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTargetBlobCount(testCase.targetBlobCount).TestObject;

        BlockHeader parentHeader = Build.A.BlockHeader
            .WithBlobGasUsed(BlobGasCalculator.CalculateBlobGas(testCase.parentBlobsCount))
            .WithExcessBlobGas(testCase.parentExcessBlobGas).TestObject;

        Assert.That(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec, header), Is.EqualTo(areBlobsEnabled ? testCase.expectedExcessBlobGas : null));
    }

    [TestCaseSource(nameof(ExcessBlobGasTestCaseSource))]
    public void Excess_blob_gas_is_calculated_properly((ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas, ulong? targetBlobCount) testCase)
    {
        TestExcessBlobGas(Homestead.Instance, false, testCase);
        TestExcessBlobGas(Frontier.Instance, false, testCase);
        TestExcessBlobGas(SpuriousDragon.Instance, false, testCase);
        TestExcessBlobGas(TangerineWhistle.Instance, false, testCase);
        TestExcessBlobGas(Byzantium.Instance, false, testCase);
        TestExcessBlobGas(Constantinople.Instance, false, testCase);
        TestExcessBlobGas(ConstantinopleFix.Instance, false, testCase);
        TestExcessBlobGas(Istanbul.Instance, false, testCase);
        TestExcessBlobGas(MuirGlacier.Instance, false, testCase);
        TestExcessBlobGas(Berlin.Instance, false, testCase);
        TestExcessBlobGas(GrayGlacier.Instance, false, testCase);
        TestExcessBlobGas(Shanghai.Instance, false, testCase);
        TestExcessBlobGas(Cancun.Instance, true, testCase);
    }

    [TestCaseSource(nameof(ExcessBlobGasTestCaseSourceForEip7742))]
    public void Excess_blob_gas_is_calculated_properly_for_eip7742((ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas, ulong? targetBlobCount) testCase)
    {
        TestExcessBlobGas(Prague.Instance, true, testCase);
    }

    [TestCaseSource(nameof(BlobGasCostTestCaseSource))]
    public void Blob_gas_price_is_calculated_properly(
        (Transaction tx, ulong excessBlobGas, UInt256 expectedCost) testCase)
    {
        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(testCase.excessBlobGas).TestObject;

        bool success = BlobGasCalculator.TryCalculateBlobGasPrice(header, testCase.tx, out UInt256 blobGasPrice);

        Assert.That(success, Is.True);
        Assert.That(blobGasPrice, Is.EqualTo(testCase.expectedCost));
    }

    [Test]
    public void Blob_gas_price_may_overflow()
    {
        var tx = Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(ulong.MaxValue).TestObject;

        bool success = BlobGasCalculator.TryCalculateBlobGasPrice(header, tx, out UInt256 blobGasPrice);

        Assert.That(success, Is.False);
        Assert.That(blobGasPrice, Is.EqualTo(UInt256.MaxValue));
    }

    public static IEnumerable<(ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas, ulong? targetBlobCount)> ExcessBlobGasTestCaseSource()
    {
        yield return (0, 0, 0, null);
        yield return (0, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob) - 1, 0, null);
        yield return (0, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob), 0, null);
        yield return (100000, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob), 100000, null);
        yield return (0, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob) + 1, Eip4844Constants.GasPerBlob * 1, null);
        yield return (Eip4844Constants.TargetBlobGasPerBlock, 1, Eip4844Constants.GasPerBlob * 1, null);
        yield return (Eip4844Constants.TargetBlobGasPerBlock, 0, 0, null);
        yield return (Eip4844Constants.TargetBlobGasPerBlock, 2, Eip4844Constants.GasPerBlob * 2, null);
        yield return (Eip4844Constants.MaxBlobGasPerBlock, 1, Eip4844Constants.TargetBlobGasPerBlock + Eip4844Constants.GasPerBlob * 1, null);
        yield return (
            Eip4844Constants.MaxBlobGasPerBlock,
            (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob),
            Eip4844Constants.MaxBlobGasPerBlock, null);
        yield return (
            Eip4844Constants.MaxBlobGasPerBlock,
            (int)(Eip4844Constants.MaxBlobGasPerBlock / Eip4844Constants.GasPerBlob),
            Eip4844Constants.MaxBlobGasPerBlock * 2 - Eip4844Constants.TargetBlobGasPerBlock, null
            );
    }

    public static IEnumerable<(ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas, ulong? targetBlobCount)> ExcessBlobGasTestCaseSourceForEip7742()
    {
        yield return (
            Eip4844Constants.MaxBlobGasPerBlock,
            1,
            Eip4844Constants.MaxBlobGasPerBlock, 1);
        yield return (
            Eip4844Constants.MaxBlobGasPerBlock,
            2,
            Eip4844Constants.MaxBlobGasPerBlock + Eip4844Constants.GasPerBlob, 1);
        yield return (0, 1, 0, 2);
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
