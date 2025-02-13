// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using MathNet.Numerics.Random;
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

    [Flags]
    public enum GasOptions
    {
        None = 0,
        AfterRepricing = 1,
        FloorCostEnabled = 2,
    }

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

        public static IEnumerable<(byte[] Data, int OldCost, int NewCost, int FloorCost)> DataTestCaseSource()
        {
            yield return ([0], 4, 4, 21010);
            yield return ([1], 68, 16, 21040);
            yield return ([0, 0, 1], 76, 24, 21060);
            yield return ([1, 1, 0], 140, 36, 21090);
            yield return ([0, 0, 1, 1], 144, 40, 21100);
        }
        [TestCaseSource(nameof(TestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((Transaction Tx, long Cost, string Description) testCase)
        {
            IntrinsicGas gas = IntrinsicGasCalculator.Calculate(testCase.Tx, Berlin.Instance);
            gas.Should().Be(new IntrinsicGas(Standard: testCase.Cost, FloorGas: 0));
        }

        [TestCaseSource(nameof(AccessTestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((List<object> orderQueue, long Cost) testCase)
        {
            AccessList.Builder accessListBuilder = new();
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

            AccessList accessList = accessListBuilder.Build();
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithAccessList(accessList).TestObject;

            void Test(IReleaseSpec spec, bool supportsAccessLists)
            {
                if (!supportsAccessLists)
                {
                    Assert.Throws<InvalidDataException>(() => IntrinsicGasCalculator.Calculate(tx, spec));
                }
                else
                {
                    IntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);
                    gas.Should().Be(new IntrinsicGas(Standard: 21000 + testCase.Cost, FloorGas: 0), spec.Name);
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
        public void Intrinsic_cost_of_data_is_calculated_properly((byte[] Data, int OldCost, int NewCost, int FloorCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;


            void Test(IReleaseSpec spec, GasOptions options)
            {
                IntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

                bool isAfterRepricing = options.HasFlag(GasOptions.AfterRepricing);
                bool floorCostEnabled = options.HasFlag(GasOptions.FloorCostEnabled);

                gas.Standard.Should()
                    .Be(21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost), spec.Name,
                        testCase.Data.ToHexString());
                gas.FloorGas.Should().Be(floorCostEnabled ? testCase.FloorCost : 0);

                gas.Should().Be(new IntrinsicGas(
                        Standard: 21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost),
                        FloorGas: floorCostEnabled ? testCase.FloorCost : 0),
                    spec.Name, testCase.Data.ToHexString());
            }

            Test(Homestead.Instance, GasOptions.None);
            Test(Frontier.Instance, GasOptions.None);
            Test(SpuriousDragon.Instance, GasOptions.None);
            Test(TangerineWhistle.Instance, GasOptions.None);
            Test(Byzantium.Instance, GasOptions.None);
            Test(Constantinople.Instance, GasOptions.None);
            Test(ConstantinopleFix.Instance, GasOptions.None);
            Test(Istanbul.Instance, GasOptions.AfterRepricing);
            Test(MuirGlacier.Instance, GasOptions.AfterRepricing);
            Test(Berlin.Instance, GasOptions.AfterRepricing);
            Test(GrayGlacier.Instance, GasOptions.AfterRepricing);
            Test(Shanghai.Instance, GasOptions.AfterRepricing);
            Test(Cancun.Instance, GasOptions.AfterRepricing);
            Test(Prague.Instance, GasOptions.AfterRepricing | GasOptions.FloorCostEnabled);
        }

        public static IEnumerable<(AuthorizationTuple[] contractCode, long expectedCost)> AuthorizationListTestCaseSource()
        {
            yield return (
                [], 0);
            yield return (
                [new AuthorizationTuple(
                    TestContext.CurrentContext.Random.NextULong(),
                    new Address(TestContext.CurrentContext.Random.NextBytes(20)),
                    TestContext.CurrentContext.Random.NextULong(),
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)))
                ],
                GasCostOf.NewAccount);
            yield return (
               [new AuthorizationTuple(
                   TestContext.CurrentContext.Random.NextULong(),
                    new Address(TestContext.CurrentContext.Random.NextBytes(20)),
                    TestContext.CurrentContext.Random.NextULong(),
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10))),
                   new AuthorizationTuple(
                    TestContext.CurrentContext.Random.NextULong(),
                    new Address(TestContext.CurrentContext.Random.NextBytes(20)),
                    TestContext.CurrentContext.Random.NextULong(),
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)))
               ],
               GasCostOf.NewAccount * 2);
            yield return (
               [new AuthorizationTuple(
                    TestContext.CurrentContext.Random.NextULong(),
                    new Address(TestContext.CurrentContext.Random.NextBytes(20)),
                    TestContext.CurrentContext.Random.NextULong(),
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10))),
                   new AuthorizationTuple(
                    TestContext.CurrentContext.Random.NextULong(),
                    new Address(TestContext.CurrentContext.Random.NextBytes(20)),
                    TestContext.CurrentContext.Random.NextULong(),
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10))),
                   new AuthorizationTuple(
                    TestContext.CurrentContext.Random.NextULong(),
                    new Address(TestContext.CurrentContext.Random.NextBytes(20)),
                    TestContext.CurrentContext.Random.NextULong(),
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)))
               ],
               GasCostOf.NewAccount * 3);
        }
        [TestCaseSource(nameof(AuthorizationListTestCaseSource))]
        public void Calculate_TxHasAuthorizationList_ReturnsExpectedCostOfTx((AuthorizationTuple[] AuthorizationList, long ExpectedCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithAuthorizationCode(testCase.AuthorizationList)
                .TestObject;

            IntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Prague.Instance);
            gas.Standard.Should().Be(GasCostOf.Transaction + (testCase.ExpectedCost));
        }

        [Test]
        public void Calculate_TxHasAuthorizationListBeforePrague_ThrowsInvalidDataException()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithAuthorizationCode(
                new AuthorizationTuple(
                    0,
                    TestItem.AddressF,
                    0,
                    TestContext.CurrentContext.Random.NextByte(),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)),
                    new UInt256(TestContext.CurrentContext.Random.NextBytes(10)))
                )
                .TestObject;

            Assert.That(() => IntrinsicGasCalculator.Calculate(tx, Cancun.Instance), Throws.InstanceOf<InvalidDataException>());
        }
    }
}
