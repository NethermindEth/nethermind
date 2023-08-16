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
using Nethermind.Verkle.Tree;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class IntrinsicGasCalculatorTests
    {
        private const long IntrinsicWitnessGasCode = 13300;
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
            VerkleWitness witness = new VerkleWitness();
            IntrinsicGasCalculator.Calculate(testCase.Tx, Prague.Instance, ref witness).Should().Be(testCase.Cost + IntrinsicWitnessGasCode);
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
                    if (spec.IsVerkleTreeEipEnabled)
                    {
                        VerkleWitness witness = new VerkleWitness();
                        IntrinsicGasCalculator.Calculate(tx, spec, ref witness).Should().Be(21000 + IntrinsicWitnessGasCode + testCase.Cost, spec.Name);
                    }
                    else
                    {
                        IntrinsicGasCalculator.Calculate(tx, spec).Should().Be(21000 + testCase.Cost, spec.Name);
                    }
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
            Test(Prague.Instance, true);
        }

        [TestCaseSource(nameof(DataTestCaseSource))]
        public void Intrinsic_cost_of_data_is_calculated_properly((byte[] Data, int OldCost, int NewCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;

            void Test(IReleaseSpec spec, bool isAfterRepricing)
            {
                long expectedGas = 21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost);
                long actualGas;
                switch (spec.IsVerkleTreeEipEnabled)
                {
                    case true:
                        expectedGas += IntrinsicWitnessGasCode;
                        VerkleWitness witness = new VerkleWitness();
                        actualGas = IntrinsicGasCalculator.Calculate(tx, spec, ref witness);
                        break;
                    case false:
                        actualGas = IntrinsicGasCalculator.Calculate(tx, spec);
                        break;
                }

                actualGas.Should()
                    .Be(expectedGas, spec.Name, testCase.Data.ToHexString());

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
            Test(Prague.Instance, true);
        }
    }
}
