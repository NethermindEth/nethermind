// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
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
        public static IEnumerable<(Transaction Tx, ulong cost, string Description)> TestCaseSource()
        {
            yield return (Build.A.Transaction.SignedAndResolved().TestObject, 21000UL, "empty");
        }

        public static IEnumerable<(List<object> orderQueue, ulong Cost)> AccessTestCaseSource()
        {
            yield return (new List<object> { }, 0UL);
            yield return (new List<object> { Address.Zero }, 2400UL);
            yield return (new List<object> { Address.Zero, (UInt256)1 }, 4300UL);
            yield return (new List<object> { Address.Zero, (UInt256)1, TestItem.AddressA, (UInt256)1 }, 8600UL);
            yield return (new List<object> { Address.Zero, (UInt256)1, Address.Zero, (UInt256)1 }, 8600UL);
        }

        public static IEnumerable<(byte[] Data, ulong OldCost, ulong NewCost, ulong FloorCost)> DataTestCaseSource()
        {
            yield return ([0], 4UL, 4UL, 21010UL);
            yield return ([1], 68UL, 16UL, 21040UL);
            yield return ([0, 0, 1], 76UL, 24UL, 21060UL);
            yield return ([1, 1, 0], 140UL, 36UL, 21090UL);
            yield return ([0, 0, 1, 1], 144UL, 40UL, 21100UL);
        }
        [TestCaseSource(nameof(TestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((Transaction Tx, ulong Cost, string Description) testCase)
        {
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(testCase.Tx, Berlin.Instance);
            Assert.That(gas, Is.EqualTo(new EthereumIntrinsicGas(Standard: testCase.Cost, FloorGas: 0)));
        }

        [TestCaseSource(nameof(AccessTestCaseSource))]
        public void Intrinsic_cost_is_calculated_properly((List<object> orderQueue, ulong Cost) testCase)
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
                    EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);
                    Assert.That(gas, Is.EqualTo(new EthereumIntrinsicGas(Standard: 21000 + testCase.Cost, FloorGas: 0)), spec.Name);
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
        public void Intrinsic_cost_of_data_is_calculated_properly((byte[] Data, ulong OldCost, ulong NewCost, ulong FloorCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;


            void Test(IReleaseSpec spec, GasOptions options)
            {
                EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

                bool isAfterRepricing = options.HasFlag(GasOptions.AfterRepricing);
                bool floorCostEnabled = options.HasFlag(GasOptions.FloorCostEnabled);

                Assert.That(gas.Standard, Is.EqualTo(21000UL + (isAfterRepricing ? testCase.NewCost : testCase.OldCost)), $"{spec.Name}: {testCase.Data.ToHexString()}");
                Assert.That(gas.FloorGas, Is.EqualTo(floorCostEnabled ? testCase.FloorCost : 0UL));

                Assert.That(gas, Is.EqualTo(new EthereumIntrinsicGas(
                        Standard: 21000UL + (isAfterRepricing ? testCase.NewCost : testCase.OldCost),
                        FloorGas: floorCostEnabled ? testCase.FloorCost : 0UL)), $"{spec.Name}: {testCase.Data.ToHexString()}");
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

        public static IEnumerable<(AuthorizationTuple[] contractCode, ulong expectedCost)> AuthorizationListTestCaseSource()
        {
            yield return (
                [], 0UL);
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
        public void Calculate_TxHasAuthorizationList_ReturnsExpectedCostOfTx((AuthorizationTuple[] AuthorizationList, ulong ExpectedCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithAuthorizationCode(testCase.AuthorizationList)
                .TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Prague.Instance);
            Assert.That(gas.Standard, Is.EqualTo(GasCostOf.Transaction + (testCase.ExpectedCost)));
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
        public void Eip8037_policy_intrinsic_gas_splits_authorization_cost()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
                .TestObject;
            IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Amsterdam.Instance);

            Assert.That(intrinsicGas.Standard.Value, Is.EqualTo(GasCostOf.Transaction + GasCostOf.PerAuthBaseRegular));
            Assert.That(intrinsicGas.Standard.StateReservoir, Is.EqualTo(GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState));
        }

        [Test]
        public void Eip8037_nongeneric_intrinsic_gas_includes_state_gas_for_create()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithCode(Array.Empty<byte>())
                .TestObject;
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);

            ulong expectedRegular = GasCostOf.Transaction + GasCostOf.CreateRegular;
            ulong expectedState = GasCostOf.CreateState;
            Assert.That(gas.Standard, Is.EqualTo(expectedRegular + expectedState));
            Assert.That(gas.MinimalGas, Is.EqualTo(Math.Max(gas.Standard, gas.FloorGas)));
        }

        [Test]
        public void Eip8037_nongeneric_intrinsic_gas_includes_state_gas_for_setcode()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
                .TestObject;
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);

            ulong expectedRegular = GasCostOf.Transaction + GasCostOf.PerAuthBaseRegular;
            ulong expectedState = GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState;
            Assert.That(gas.Standard, Is.EqualTo(expectedRegular + expectedState));
        }

        [Test]
        public void Eip8037_nongeneric_minimal_gas_is_at_least_regular_plus_state()
        {
            // A create tx with no calldata: floor gas is low, Standard = regular + state
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithCode(Array.Empty<byte>())
                .TestObject;
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);

            ulong regularPlusState = GasCostOf.Transaction + GasCostOf.CreateRegular + GasCostOf.CreateState;
            Assert.That(gas.MinimalGas, Is.GreaterThanOrEqualTo(regularPlusState));
        }

        private const ulong TxBaseEip2780 = GasCostOf.TransactionEip2780; // 4500

        public enum Recipient { NewAccount, ExistingEoa, Contract, Precompile, SelfTransfer }

        // EIP-2780 intrinsic gas is a pure function of the transaction: the recipient touch, value
        // tiers, new-account surcharge, and transfer log are charged at execution (see
        // TransactionProcessor), so intrinsic is identical regardless of recipient kind or value.
        [TestCase(Recipient.NewAccount)]
        [TestCase(Recipient.ExistingEoa)]
        [TestCase(Recipient.Contract)]
        [TestCase(Recipient.Precompile)]
        [TestCase(Recipient.SelfTransfer)]
        public void Eip2780_intrinsic_is_state_independent(Recipient recipient)
        {
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            Address to = recipient switch
            {
                Recipient.Precompile => Address.FromNumber(1), // 0x01 ECRECOVER precompile
                Recipient.SelfTransfer => TestItem.AddressA,   // == sender (PrivateKeyA)
                _ => TestItem.AddressB,
            };
            Transaction tx = Build.A.Transaction.WithValue(1).WithTo(to)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

            Assert.That(gas.Standard, Is.EqualTo(TxBaseEip2780));
        }

        [Test]
        public void Eip2780_reduces_the_calldata_floor_base()
        {
            // Without reducing the floor base, the legacy 21,000 floor would dominate and negate the EIP.
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            Transaction tx = Build.A.Transaction.WithData([1]).SignedAndResolved().TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

            // Same per-token floor as Prague (DataTestCaseSource maps [1] to 21,040), rebased to 4,500.
            Assert.That(gas.FloorGas, Is.EqualTo(TxBaseEip2780 + (21040ul - GasCostOf.Transaction)));
        }

        [Test]
        public void Eip2780_disabled_keeps_legacy_intrinsic_base()
        {
            Transaction tx = Build.A.Transaction.WithValue(1).WithTo(TestItem.AddressB)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Prague.Instance);

            Assert.That(gas.Standard, Is.EqualTo(GasCostOf.Transaction));
        }
    }
}
