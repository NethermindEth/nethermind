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
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
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

            // Recipient touch: COLD + TRANSFER_LOG + TX_VALUE; authorization: state-independent base.
            ulong recipientRegular = Eip8038Constants.ColdAccountAccess + GasCostOf.TransferLogEip2780 + GasCostOf.TxValueCostEip2780;
            Assert.That(intrinsicGas.Standard.Value, Is.EqualTo(GasCostOf.TransactionEip2780 + recipientRegular + Eip8038Constants.PerAuthBaseRegular));
            Assert.That(intrinsicGas.Standard.StateReservoir, Is.Zero);
        }

        [Test]
        public void Eip8037_nongeneric_intrinsic_gas_excludes_top_frame_state_gas_for_create()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithCode(Array.Empty<byte>())
                .TestObject;
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);

            // Create regular = CREATE_ACCESS + TRANSFER_LOG (value endowment); NEW_ACCOUNT is top-frame state gas.
            ulong expectedRegular = GasCostOf.TransactionEip2780 + Eip8038Constants.CreateAccess + GasCostOf.TransferLogEip2780;
            Assert.That(gas.Standard, Is.EqualTo(expectedRegular));
            Assert.That(gas.MinimalGas, Is.EqualTo(Math.Max(gas.Standard, gas.FloorGas)));
        }

        [Test]
        public void Eip8037_nongeneric_intrinsic_gas_excludes_top_frame_state_gas_for_setcode()
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
                .TestObject;
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);

            ulong recipientRegular = Eip8038Constants.ColdAccountAccess + GasCostOf.TransferLogEip2780 + GasCostOf.TxValueCostEip2780;
            ulong expectedRegular = GasCostOf.TransactionEip2780 + recipientRegular + Eip8038Constants.PerAuthBaseRegular;
            Assert.That(gas.Standard, Is.EqualTo(expectedRegular));
        }

        [Test]
        public void Eip8037_nongeneric_minimal_gas_is_at_least_regular_intrinsic()
        {
            // A create tx with no calldata: floor gas is low, Standard = regular intrinsic.
            Transaction tx = Build.A.Transaction.SignedAndResolved()
                .WithCode(Array.Empty<byte>())
                .TestObject;
            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);

            ulong regular = GasCostOf.TransactionEip2780 + Eip8038Constants.CreateAccess + GasCostOf.TransferLogEip2780;
            Assert.That(gas.MinimalGas, Is.GreaterThanOrEqualTo(regular));
        }

        // EIP-2780 fixed-cost vectors: the intrinsic is state-independent, so the recipient
        // touch and value-move costs are flat for every non-self recipient.
        private const ulong TxBaseEip2780 = GasCostOf.TransactionEip2780;
        private const ulong TransferLogEip2780 = GasCostOf.TransferLogEip2780;
        private const ulong ColdAccess = Eip8038Constants.ColdAccountAccess;
        private const ulong TxValueCost = GasCostOf.TxValueCostEip2780;

        [TestCase(false, 1ul, TxBaseEip2780 + ColdAccess + TxValueCost + TransferLogEip2780, TestName = "Eip2780_intrinsic_value_transfer_21000")]
        [TestCase(true, 1ul, TxBaseEip2780, TestName = "Eip2780_intrinsic_self_transfer_12000")]
        [TestCase(false, 0ul, TxBaseEip2780 + ColdAccess, TestName = "Eip2780_intrinsic_no_transfer_15000")]
        [TestCase(true, 0ul, TxBaseEip2780, TestName = "Eip2780_intrinsic_self_no_transfer_12000")]
        public void Eip2780_intrinsic_gas_is_calculated_properly(bool selfTransfer, ulong value, ulong expectedStandard)
        {
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            Address to = selfTransfer ? TestItem.AddressA : TestItem.AddressB; // AddressA == sender (PrivateKeyA)
            Transaction tx = Build.A.Transaction.WithValue(value).WithTo(to)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

            Assert.That(gas.Standard, Is.EqualTo(expectedStandard));
            Assert.That(gas.MinimalGas, Is.EqualTo(Math.Max(expectedStandard, TxBaseEip2780)));
        }

        [Test]
        public void Eip2780_access_list_does_not_discount_the_flat_recipient_touch()
        {
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            AccessList accessList = new AccessList.Builder().AddAddress(TestItem.AddressB).Build();
            Transaction tx = Build.A.Transaction.WithValue(1).WithTo(TestItem.AddressB).WithAccessList(accessList)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            ulong actual = IntrinsicGasCalculator.Calculate(tx, spec).Standard;

            Assert.That(actual, Is.EqualTo(TxBaseEip2780 + GasCostOf.AccessAccountListEntry + ColdAccess + TxValueCost + TransferLogEip2780));
        }

        [Test]
        public void Eip2780_intrinsic_gas_for_create_charges_transfer_log_only_when_value_positive()
        {
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };

            Transaction createZero = Build.A.Transaction.WithValue(0).WithCode(Array.Empty<byte>())
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction createValue = Build.A.Transaction.WithValue(1).WithCode(Array.Empty<byte>())
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Assert.That(IntrinsicGasCalculator.Calculate(createZero, spec).Standard,
                Is.EqualTo(TxBaseEip2780 + GasCostOf.TxCreate));
            // CREATE endows a fresh, sender-distinct address, so value > 0 pays the transfer log.
            Assert.That(IntrinsicGasCalculator.Calculate(createValue, spec).Standard,
                Is.EqualTo(TxBaseEip2780 + GasCostOf.TxCreate + TransferLogEip2780));
        }

        [Test]
        public void Eip2780_reduces_the_calldata_floor_base()
        {
            // Without reducing the floor base, the legacy 21,000 floor would dominate and negate the EIP.
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            Transaction tx = Build.A.Transaction.WithData([1]).SignedAndResolved().TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

            // EIP-2780 uses its reduced transaction base plus the non-self recipient touch in the floor base.
            Assert.That(gas.FloorGas, Is.EqualTo(TxBaseEip2780 + ColdAccess + 6_040));
        }

        [Test]
        public void Eip2780_disabled_keeps_legacy_intrinsic_base()
        {
            Transaction tx = Build.A.Transaction.WithValue(1).WithTo(TestItem.AddressB)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Prague.Instance);

            Assert.That(gas.Standard, Is.EqualTo(GasCostOf.Transaction));
        }

        [Test]
        public void Calculate_Eip7623FloorWithoutEip2028_UsesFixedTokenCost()
        {
            ReleaseSpec spec = new()
            {
                IsEip2Enabled = true,
                IsEip7623Enabled = true,
                IsEip2028Enabled = false,
            };

            byte[] data = new byte[30];
            for (int i = 0; i < 10; i++) data[i] = 0x01; // 10 non-zero, 20 zero bytes
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(data).TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

            const ulong zeros = 20, nonZeros = 10;
            ulong expectedFloor = GasCostOf.Transaction
                + GasCostOf.TotalCostFloorPerTokenEip7623 * (zeros + nonZeros * GasCostOf.TxDataNonZeroMultiplierEip2028);
            ulong expectedStandard = GasCostOf.Transaction
                + (zeros + nonZeros * spec.GasCosts.TxDataNonZeroMultiplier) * GasCostOf.TxDataZero;

            Assert.That(gas.FloorGas, Is.EqualTo(expectedFloor));   // 21600
            Assert.That(gas.Standard, Is.EqualTo(expectedStandard)); // 21760, still 68/non-zero byte
        }
    }
}
