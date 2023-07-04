// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
public class DataGasCalculatorTests
{
    [TestCaseSource(nameof(ExcessDataGasTestCaseSource))]
    public void Excess_data_gas_is_calculated_properly((ulong parentExcessDataGas, int parentBlobsCount, ulong expectedExcessDataGas) testCase)
    {
        void Test(IReleaseSpec spec, bool areBlobsEnabled)
        {
            BlockHeader parentHeader = Build.A.BlockHeader
                .WithDataGasUsed(DataGasCalculator.CalculateDataGas(testCase.parentBlobsCount))
                .WithExcessDataGas(testCase.parentExcessDataGas).TestObject;

            Assert.That(DataGasCalculator.CalculateExcessDataGas(parentHeader, spec), Is.EqualTo(areBlobsEnabled ? testCase.expectedExcessDataGas : null));
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

    [TestCaseSource(nameof(BlobDataGasCostTestCaseSource))]
    public void Data_gas_price_is_calculated_properly(
        (Transaction tx, ulong excessDataGas, UInt256 expectedCost) testCase)
    {
        BlockHeader header = Build.A.BlockHeader.WithExcessDataGas(testCase.excessDataGas).TestObject;

        bool success = DataGasCalculator.TryCalculateDataGasPrice(header, testCase.tx, out UInt256 dataGasPrice);

        Assert.That(success, Is.True);
        Assert.That(dataGasPrice, Is.EqualTo(testCase.expectedCost));
    }

    [Test]
    public void Data_gas_price_may_overflow()
    {
        var tx = Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithExcessDataGas(ulong.MaxValue).TestObject;

        bool success = DataGasCalculator.TryCalculateDataGasPrice(header, tx, out UInt256 dataGasPrice);

        Assert.That(success, Is.False);
        Assert.That(dataGasPrice, Is.EqualTo(UInt256.MaxValue));
    }

    public static IEnumerable<(ulong parentExcessDataGas, int parentBlobsCount, ulong expectedExcessDataGas)> ExcessDataGasTestCaseSource()
    {
        yield return (0, 0, 0);
        yield return (0, (int)(Eip4844Constants.TargetDataGasPerBlock / Eip4844Constants.DataGasPerBlob) - 1, 0);
        yield return (0, (int)(Eip4844Constants.TargetDataGasPerBlock / Eip4844Constants.DataGasPerBlob), 0);
        yield return (100000, (int)(Eip4844Constants.TargetDataGasPerBlock / Eip4844Constants.DataGasPerBlob), 100000);
        yield return (0, (int)(Eip4844Constants.TargetDataGasPerBlock / Eip4844Constants.DataGasPerBlob) + 1, Eip4844Constants.DataGasPerBlob * 1);
        yield return (Eip4844Constants.TargetDataGasPerBlock, 1, Eip4844Constants.DataGasPerBlob * 1);
        yield return (Eip4844Constants.TargetDataGasPerBlock, 0, 0);
        yield return (Eip4844Constants.TargetDataGasPerBlock, 2, Eip4844Constants.DataGasPerBlob * 2);
        yield return (Eip4844Constants.MaxDataGasPerBlock, 1, Eip4844Constants.TargetDataGasPerBlock + Eip4844Constants.DataGasPerBlob * 1);
        yield return (
            Eip4844Constants.MaxDataGasPerBlock,
            (int)(Eip4844Constants.TargetDataGasPerBlock / Eip4844Constants.DataGasPerBlob),
            Eip4844Constants.MaxDataGasPerBlock);
        yield return (
            Eip4844Constants.MaxDataGasPerBlock,
            (int)(Eip4844Constants.MaxDataGasPerBlock / Eip4844Constants.DataGasPerBlob),
            Eip4844Constants.MaxDataGasPerBlock * 2 - Eip4844Constants.TargetDataGasPerBlock
            );
    }

    public static IEnumerable<(Transaction tx, ulong excessDataGas, UInt256 expectedCost)> BlobDataGasCostTestCaseSource()
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
