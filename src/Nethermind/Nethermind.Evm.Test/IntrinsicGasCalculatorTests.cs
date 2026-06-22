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
            Assert.That(gas, Is.EqualTo(new EthereumIntrinsicGas(Standard: testCase.Cost, FloorGas: 0)));
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
        public void Intrinsic_cost_of_data_is_calculated_properly((byte[] Data, int OldCost, int NewCost, int FloorCost) testCase)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved().WithData(testCase.Data).TestObject;


            void Test(IReleaseSpec spec, GasOptions options)
            {
                EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

                bool isAfterRepricing = options.HasFlag(GasOptions.AfterRepricing);
                bool floorCostEnabled = options.HasFlag(GasOptions.FloorCostEnabled);

                Assert.That(gas.Standard, Is.EqualTo(21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost)), $"{spec.Name}: {testCase.Data.ToHexString()}");
                Assert.That(gas.FloorGas, Is.EqualTo(floorCostEnabled ? testCase.FloorCost : 0));

                Assert.That(gas, Is.EqualTo(new EthereumIntrinsicGas(
                        Standard: 21000 + (isAfterRepricing ? testCase.NewCost : testCase.OldCost),
                        FloorGas: floorCostEnabled ? testCase.FloorCost : 0)), $"{spec.Name}: {testCase.Data.ToHexString()}");
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

            long expectedRegular = GasCostOf.Transaction + GasCostOf.CreateRegular;
            long expectedState = GasCostOf.CreateState;
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

            long expectedRegular = GasCostOf.Transaction + GasCostOf.PerAuthBaseRegular;
            long expectedState = GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState;
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

            long regularPlusState = GasCostOf.Transaction + GasCostOf.CreateRegular + GasCostOf.CreateState;
            Assert.That(gas.MinimalGas, Is.GreaterThanOrEqualTo(regularPlusState));
        }

        // EIP-2780 total fixed-cost vectors (TX_BASE_COST + transfer log + new-account surcharge +
        // recipient cold/warm touch + value STATE_UPDATE), matching the spec's reference-case table.
        public enum Recipient { NewAccount, ExistingEoa, Contract, Precompile, SelfTransfer, EmptyZeroValue }

        private const long TxBaseEip2780 = GasCostOf.TransactionEip2780;        // 4500
        private const long TransferLogEip2780 = GasCostOf.TransferLogEip2780;   // 1756
        private const long ColdNoCode = GasCostOf.ColdAccountAccessNoCodeEip2780; // 500
        private const long ColdCode = GasCostOf.ColdAccountAccess;              // 2600
        private const long StateUpdate = GasCostOf.StateUpdateEip2780;          // 1000

        public static IEnumerable<TestCaseData> Eip2780IntrinsicCases()
        {
            yield return new TestCaseData(Recipient.NewAccount, (UInt256)1, TxBaseEip2780 + ColdNoCode + GasCostOf.NewAccount + TransferLogEip2780)
                .SetName("Eip2780_intrinsic_value_to_new_account_31756");
            yield return new TestCaseData(Recipient.ExistingEoa, (UInt256)1, TxBaseEip2780 + ColdNoCode + StateUpdate + TransferLogEip2780)
                .SetName("Eip2780_intrinsic_value_to_existing_eoa_7756");
            yield return new TestCaseData(Recipient.Contract, (UInt256)1, TxBaseEip2780 + ColdCode + StateUpdate + TransferLogEip2780)
                .SetName("Eip2780_intrinsic_value_to_contract_9856");
            yield return new TestCaseData(Recipient.Precompile, (UInt256)1, TxBaseEip2780 + TransferLogEip2780)
                .SetName("Eip2780_intrinsic_value_to_precompile_6256");
            yield return new TestCaseData(Recipient.SelfTransfer, (UInt256)1, TxBaseEip2780)
                .SetName("Eip2780_intrinsic_self_transfer_4500");
            yield return new TestCaseData(Recipient.EmptyZeroValue, (UInt256)0, TxBaseEip2780 + ColdNoCode)
                .SetName("Eip2780_intrinsic_no_transfer_to_empty_5000");
        }

        [TestCaseSource(nameof(Eip2780IntrinsicCases))]
        public void Eip2780_intrinsic_gas_is_calculated_properly(Recipient recipient, UInt256 value, long expectedStandard)
        {
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            Address to = recipient switch
            {
                Recipient.Precompile => Address.FromNumber(1), // 0x01 ECRECOVER precompile
                Recipient.SelfTransfer => TestItem.AddressA,   // == sender (PrivateKeyA)
                _ => TestItem.AddressB,
            };
            Transaction tx = Build.A.Transaction.WithValue(value).WithTo(to)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
            // Only an unfunded recipient is nonexistent per EIP-161 (drives the new-account surcharge).
            state.IsDeadAccount(Arg.Any<Address>()).Returns(recipient is Recipient.NewAccount);
            state.IsContract(Arg.Any<Address>()).Returns(recipient is Recipient.Contract);

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec, 0, state);

            Assert.That(gas.Standard, Is.EqualTo(expectedStandard));
            Assert.That(gas.MinimalGas, Is.EqualTo(Math.Max(expectedStandard, TxBaseEip2780)));
        }

        [Test]
        public void Eip2780_recipient_warm_via_access_list_is_charged_warm_read()
        {
            // EIP-2780 test vector 7: a recipient present in the access list is touched at WARM_STATE_READ.
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            AccessList accessList = new AccessList.Builder().AddAddress(TestItem.AddressB).Build();
            Transaction tx = Build.A.Transaction.WithValue(1).WithTo(TestItem.AddressB).WithAccessList(accessList)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();

            long warmTouch = IntrinsicGasCalculator.Calculate(tx, spec, 0, state).Standard;

            long expected = TxBaseEip2780 + GasCostOf.AccessAccountListEntry + GasCostOf.WarmStateRead + StateUpdate + TransferLogEip2780;
            Assert.That(warmTouch, Is.EqualTo(expected));
        }

        [Test]
        public void Eip2780_intrinsic_gas_for_create_charges_transfer_log_only_when_value_positive()
        {
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();

            Transaction createZero = Build.A.Transaction.WithValue(0).WithCode(Array.Empty<byte>())
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction createValue = Build.A.Transaction.WithValue(1).WithCode(Array.Empty<byte>())
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Assert.That(IntrinsicGasCalculator.Calculate(createZero, spec, 0, state).Standard,
                Is.EqualTo(TxBaseEip2780 + GasCostOf.TxCreate));
            // CREATE endows a fresh, sender-distinct address, so value > 0 pays the transfer log.
            Assert.That(IntrinsicGasCalculator.Calculate(createValue, spec, 0, state).Standard,
                Is.EqualTo(TxBaseEip2780 + GasCostOf.TxCreate + TransferLogEip2780));
        }

        [Test]
        public void Eip2780_reduces_the_calldata_floor_base()
        {
            // Without reducing the floor base, the legacy 21,000 floor would dominate and negate the EIP.
            OverridableReleaseSpec spec = new(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true };
            Transaction tx = Build.A.Transaction.WithData([1]).SignedAndResolved().TestObject;

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, spec);

            // Same per-token floor as Prague (DataTestCaseSource maps [1] to 21,040), rebased to 4,500.
            Assert.That(gas.FloorGas, Is.EqualTo(TxBaseEip2780 + (21040 - GasCostOf.Transaction)));
        }

        [Test]
        public void Eip2780_disabled_keeps_legacy_intrinsic_base()
        {
            Transaction tx = Build.A.Transaction.WithValue(1).WithTo(TestItem.AddressB)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
            state.IsDeadAccount(Arg.Any<Address>()).Returns(true);

            EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Prague.Instance, 0, state);

            Assert.That(gas.Standard, Is.EqualTo(GasCostOf.Transaction));
        }
    }
}
