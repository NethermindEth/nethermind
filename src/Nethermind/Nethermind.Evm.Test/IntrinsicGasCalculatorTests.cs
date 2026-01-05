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
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(testCase.Tx, Berlin.Instance);
            gas.Should().Be(new EthereumIntrinsicGas(Standard: testCase.Cost, FloorGas: 0));
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
                    tx.ClearCachedIntrinsicGas();
                    EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);
                    gas.Should().Be(new EthereumIntrinsicGas(Standard: 21000 + testCase.Cost, FloorGas: 0), spec.Name);
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
                tx.ClearCachedIntrinsicGas();
                EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

                bool isAfterRepricing = options.HasFlag(GasOptions.AfterRepricing);
                bool floorCostEnabled = options.HasFlag(GasOptions.FloorCostEnabled);

                gas.Standard.Should()
                    .Be(21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost), spec.Name,
                        testCase.Data.ToHexString());
                gas.FloorGas.Should().Be(floorCostEnabled ? testCase.FloorCost : 0);

                gas.Should().Be(new EthereumIntrinsicGas(
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
            Test(Istanbul.Instance, GasOptions.AfterRepricing); // suspect - fails with cache
            Test(MuirGlacier.Instance, GasOptions.AfterRepricing); // suspect - fails with cache
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

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Prague.Instance);
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

        [Test]
        public void Calculate_sets_cache_on_transaction()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            
            var result1 = IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            
            transaction.TryGetCachedIntrinsicGas(out long cachedStandard, out long cachedFloorGas).Should().BeTrue();
            cachedStandard.Should().Be(result1.Standard);
            cachedFloorGas.Should().Be(result1.FloorGas);
        }

        [Test]
        public void Calculate_uses_cache_on_second_call()
        {
            var transaction = Build.A.Transaction
                .WithData(new byte[100])
                .SignedAndResolved()
                .TestObject;
            
            var result1 = IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            
            var result2 = IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            
            result2.Should().Be(result1);
        }

        [Test]
        public void Calculate_with_different_specs_uses_correct_cache_per_spec()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            
            // Both specs should give same result for simple transaction
            var resultBerlin = IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            var resultLondon = IntrinsicGasCalculator.Calculate(transaction, London.Instance);
            
            resultBerlin.Should().Be(resultLondon);
        }

        [Test]
        public void Calculate_with_authorization_list_caches_correctly()
        {
            var authList = new[]
            {
                new AuthorizationTuple(0, TestItem.AddressA, 0, 0, UInt256.Zero, UInt256.Zero)
            };
            
            var transaction = Build.A.Transaction
                .WithAuthorizationCode(authList)
                .SignedAndResolved()
                .TestObject;
            
            var result1 = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
            var result2 = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
            
            result2.Should().Be(result1);
            result1.Standard.Should().Be(21000 + GasCostOf.NewAccount);
        }

        [Test]
        public void Calculate_contract_creation_includes_create_cost()
        {
            var transaction = Build.A.Transaction
                .To(null)
                .WithData(new byte[100])
                .SignedAndResolved()
                .TestObject;
            
            var result = IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            
            result.Standard.Should().BeGreaterThan(21000);
            transaction.TryGetCachedIntrinsicGas(out long cachedStandard, out long cachedFloorGas).Should().BeTrue();
            cachedStandard.Should().Be(53400);
        }

        [Test]
        public void Calculate_floor_cost_when_EIP7623_enabled()
        {
            var transaction = Build.A.Transaction
                .WithData(new byte[100])
                .SignedAndResolved()
                .TestObject;
            
            var result = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
            
            result.FloorGas.Should().BeGreaterThan(0);
            transaction.TryGetCachedIntrinsicGas(out long cachedStandard, out long cachedFloorGas).Should().BeTrue();
            cachedFloorGas.Should().Be(result.FloorGas);
        }

        [Test]
        public void Calculate_floor_cost_zero_when_EIP7623_disabled()
        {
            var transaction = Build.A.Transaction
                .WithData(new byte[100])
                .SignedAndResolved()
                .TestObject;
            
            var result = IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
            
            result.FloorGas.Should().Be(0);
            transaction.TryGetCachedIntrinsicGas(out long cachedStandard, out long cachedFloorGas).Should().BeTrue();
            cachedFloorGas.Should().Be(0);
        }
    }
}
