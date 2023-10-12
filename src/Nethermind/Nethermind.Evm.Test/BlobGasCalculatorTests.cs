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
    [TestCaseSource(nameof(ExcessBlobGasTestCaseSource))]
    public void Excess_blob_gas_is_calculated_properly((ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas) testCase)
    {
        void Test(IReleaseSpec spec, bool areBlobsEnabled)
        {
            BlockHeader parentHeader = Build.A.BlockHeader
                .WithBlobGasUsed(BlobGasCalculator.CalculateBlobGas(testCase.parentBlobsCount))
                .WithExcessBlobGas(testCase.parentExcessBlobGas).TestObject;

            Assert.That(BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec), Is.EqualTo(areBlobsEnabled ? testCase.expectedExcessBlobGas : null));
        }

        Test(Homestead.Instance, false);
        Test(Frontier.Instance, false);
        Test(SpuriousDragon.Instance, false);
        Test(TangerineWhistle.Instance, false);
        Test(Byzantium.Instance, false);
        Test(Constantinople.Instance, false);
        Test(ConstantinopleFix.Instance, false);
        Test(Istanbul.Instance, false);
        Test(MuirGlacier.Instance, false);
        Test(Berlin.Instance, false);
        Test(GrayGlacier.Instance, false);
        Test(Shanghai.Instance, false);
        Test(Cancun.Instance, true);
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

    public static IEnumerable<(ulong parentExcessBlobGas, int parentBlobsCount, ulong expectedExcessBlobGas)> ExcessBlobGasTestCaseSource()
    {
        yield return (0, 0, 0);
        yield return (0, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob) - 1, 0);
        yield return (0, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob), 0);
        yield return (100000, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob), 100000);
        yield return (0, (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob) + 1, Eip4844Constants.GasPerBlob * 1);
        yield return (Eip4844Constants.TargetBlobGasPerBlock, 1, Eip4844Constants.GasPerBlob * 1);
        yield return (Eip4844Constants.TargetBlobGasPerBlock, 0, 0);
        yield return (Eip4844Constants.TargetBlobGasPerBlock, 2, Eip4844Constants.GasPerBlob * 2);
        yield return (Eip4844Constants.MaxBlobGasPerBlock, 1, Eip4844Constants.TargetBlobGasPerBlock + Eip4844Constants.GasPerBlob * 1);
        yield return (
            Eip4844Constants.MaxBlobGasPerBlock,
            (int)(Eip4844Constants.TargetBlobGasPerBlock / Eip4844Constants.GasPerBlob),
            Eip4844Constants.MaxBlobGasPerBlock);
        yield return (
            Eip4844Constants.MaxBlobGasPerBlock,
            (int)(Eip4844Constants.MaxBlobGasPerBlock / Eip4844Constants.GasPerBlob),
            Eip4844Constants.MaxBlobGasPerBlock * 2 - Eip4844Constants.TargetBlobGasPerBlock
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
