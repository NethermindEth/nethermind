// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class IntrinsicGasCalculatorTests
    {
        public static IEnumerable<(Transaction Tx, long cost, string Description)> TestCaseSource()
        {
            yield return (Build.A.Transaction.SignedAndResolved().TestObject, 21000, "empty");
        }

        public static IEnumerable<(List<object> orderQueue, long Cost)> AccessTestCaseSource()
        {
            yield return (new List<object> { }, 0);
            yield return (new List<object> { Address.Zero }, 2400);
            yield return (new List<object> { Address.Zero, (UInt256)1 }, 4300);
            yield return (new List<object> { Address.Zero, (UInt256)1, TestItem.AddressA, (UInt256)1 }, 8600);
            yield return (new List<object> { Address.Zero, (UInt256)1, Address.Zero, (UInt256)1 }, 8600);
        }

        public static IEnumerable<(byte[] Data, int OldCost, int NewCost)> DataTestCaseSource()
        {
            yield return (new byte[] { 0 }, 4, 4);
            yield return (new byte[] { 1 }, 68, 16);
            yield return (new byte[] { 0, 0, 1 }, 76, 24);
            yield return (new byte[] { 1, 1, 0 }, 140, 36);
            yield return (new byte[] { 0, 0, 1, 1 }, 144, 40);
        }
        [TestCaseSource(nameof(TestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((Transaction Tx, long Cost, string Description) testCase)
        {
            IntrinsicGasCalculator.Calculate(testCase.Tx, Berlin.Instance).Should().Be(testCase.Cost);
        }

        [TestCaseSource(nameof(AccessTestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((List<object> orderQueue, long Cost) testCase)
        {
            AccessListBuilder accessListBuilder = new();
            foreach (object o in testCase.orderQueue)
            {
                if (o is Address address)
                {
                    accessListBuilder.AddAddress(address);
                }
                else if (o is UInt256 index)
                {
                    accessListBuilder.AddStorage(index);
                }
            }

            AccessList accessList = accessListBuilder.ToAccessList();
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithAccessList(accessList).TestObject;

            void Test(IReleaseSpec spec, bool supportsAccessLists)
            {
                if (!supportsAccessLists)
                {
                    Assert.Throws<InvalidDataException>(() => IntrinsicGasCalculator.Calculate(tx, spec));
                }
                else
                {
                    IntrinsicGasCalculator.Calculate(tx, spec).Should().Be(21000 + testCase.Cost, spec.Name);
                }
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
            Test(Berlin.Instance, true);
        }

        [TestCaseSource(nameof(DataTestCaseSource))]
        public void Intrinsic_cost_of_data_is_calculated_properly((byte[] Data, int OldCost, int NewCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;

            void Test(IReleaseSpec spec, bool isAfterRepricing)
            {
                IntrinsicGasCalculator.Calculate(tx, spec).Should()
                    .Be(21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost), spec.Name,
                        testCase.Data.ToHexString());
            }

            Test(Homestead.Instance, false);
            Test(Frontier.Instance, false);
            Test(SpuriousDragon.Instance, false);
            Test(TangerineWhistle.Instance, false);
            Test(Byzantium.Instance, false);
            Test(Constantinople.Instance, false);
            Test(ConstantinopleFix.Instance, false);
            Test(Istanbul.Instance, true);
            Test(MuirGlacier.Instance, true);
            Test(Berlin.Instance, true);
            Test(GrayGlacier.Instance, true);
            Test(Shanghai.Instance, true);
            Test(Cancun.Instance, true);
        }

        public static IEnumerable<(UInt256 parentExcessDataGas, int newBlobsCount, UInt256 expectedCost)> ExcessDataGasTestCaseSource()
        {
            yield return (0, 0, 0);
            yield return (0, 1, 0);
            yield return (0, 2, 0);
            yield return (0, 3, Eip4844Constants.DataGasPerBlob * (3 - 2));
            yield return (100000, 3, Eip4844Constants.DataGasPerBlob + 100000);
            yield return (Eip4844Constants.TargetDataGasPerBlock, 1, Eip4844Constants.DataGasPerBlob * 1);
            yield return (Eip4844Constants.TargetDataGasPerBlock, 0, 0);
            yield return (Eip4844Constants.TargetDataGasPerBlock, 2, Eip4844Constants.DataGasPerBlob * 2);
        }

        [TestCaseSource(nameof(ExcessDataGasTestCaseSource))]
        public void Blobs_excess_data_gas_is_calculated_correctly((UInt256 parentExcessDataGas, int newBlobsCount, UInt256 expectedCost) testCase)
        {
            void Test(IReleaseSpec spec, bool areBlobsEnabled)
            {
                IntrinsicGasCalculator.CalculateExcessDataGas(testCase.parentExcessDataGas, testCase.newBlobsCount, spec).Should()
                    .Be(areBlobsEnabled ? testCase.expectedCost : null);
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

        public static IEnumerable<(Transaction tx, UInt256 parentExcessDataGas, UInt256 expectedCost)> BlobDataGasCostTestCaseSource()
        {
            yield return (Build.A.Transaction.TestObject, 0, 0);
            yield return (Build.A.Transaction.TestObject, 1000, 0);
            yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(0).TestObject, 1000, 0);
            yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1).TestObject, 0, 131072);
            yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1).TestObject, 10000000, 11665408);
            yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject, 0, 131072000);
            yield return (Build.A.Transaction.WithType(TxType.Blob).WithBlobVersionedHashes(1000).TestObject, 10000000, 11665408000);
        }

        [TestCaseSource(nameof(BlobDataGasCostTestCaseSource))]
        public void Blobs_intrinsic_cost_is_calculated_properly(
            (Transaction tx, UInt256 parentExcessDataGas, UInt256 expectedCost) testCase)
        {
            IntrinsicGasCalculator.CalculateDataGasPrice(testCase.tx, testCase.parentExcessDataGas).Should()
                .Be(testCase.expectedCost);
        }
    }
}
