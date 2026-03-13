// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2200Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;

        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [TestCase("0x60006000556000600055", 1612, 0, 0)]
        [TestCase("0x60006000556001600055", 20812, 0, 0)]
        [TestCase("0x60016000556000600055", 20812, 19200, 0)]
        [TestCase("0x60016000556002600055", 20812, 0, 0)]
        [TestCase("0x60016000556001600055", 20812, 0, 0)]
        [TestCase("0x60006000556000600055", 5812, 15000, 1)]
        [TestCase("0x60006000556001600055", 5812, 4200, 1)]
        [TestCase("0x60006000556002600055", 5812, 0, 1)]
        [TestCase("0x60026000556000600055", 5812, 15000, 1)]
        [TestCase("0x60026000556003600055", 5812, 0, 1)]
        [TestCase("0x60026000556001600055", 5812, 4200, 1)]
        [TestCase("0x60026000556002600055", 5812, 0, 1)]
        [TestCase("0x60016000556000600055", 5812, 15000, 1)]
        [TestCase("0x60016000556002600055", 5812, 0, 1)]
        [TestCase("0x60016000556001600055", 1612, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40818, 19200, 0)]
        [TestCase("0x600060005560016000556000600055", 10818, 19200, 1)]
        public void Test(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            SetupStorage(originalValue);

            TestAllTracerWithOutput receipt = Execute(Bytes.FromHexString(codeHex));
            AssertGas(receipt, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / 2, refund));
        }

        [TestCase("0x60006000556000600055", 1612, 0, 2300, true)]
        [TestCase("0x60016000556000600055", 20812, 0, 2300, true)]
        [TestCase("0x60016000556002600055", 20812, 0, 2300, true)]
        [TestCase("0x60016000556001600055", 20812, 0, 2300, true)]
        [TestCase("0x60006000556000600055", 5812, 1, 2300, true)]
        [TestCase("0x60006000556001600055", 5812, 1, 2300, true)]
        [TestCase("0x60026000556000600055", 5812, 1, 2300, true)]
        [TestCase("0x60026000556003600055", 5812, 1, 2300, true)]
        [TestCase("0x60026000556001600055", 5812, 1, 2300, true)]
        [TestCase("0x60026000556002600055", 5812, 1, 2300, true)]
        [TestCase("0x60016000556001600055", 1612, 1, 2300, true)]
        [TestCase("0x60006000556002600055", 5812, 1, 2300, true)]
        [TestCase("0x60016000556000600055", 5812, 1, 2300, false)]
        [TestCase("0x60016000556002600055", 5812, 1, 2300, false)]
        [TestCase("0x600160005560006000556001600055", 40818, 0, 2300, false)]
        [TestCase("0x600060005560016000556000600055", 10818, 1, 2300, false)]
        [TestCase("0x60006000556001600055", 20812, 0, 2300, false)]
        [TestCase("0x60006000556000600055", 1612, 0, 2301, false)]
        [TestCase("0x60016000556001600055", 1612, 1, 2301, false)]
        [TestCase("0x60006000556000600055", 1612, 0, 2299, true)]
        [TestCase("0x60016000556001600055", 1612, 1, 2299, true)]
        public void Test_at_stipend_boundary(string codeHex, long gasUsed, byte originalValue, int stipend, bool outOfGasExpected)
        {
            SetupStorage(originalValue);

            TestAllTracerWithOutput receipt = Execute(BlockNumber, 21000 + gasUsed + (stipend - 800), Bytes.FromHexString(codeHex));
            Assert.That(receipt.StatusCode, Is.EqualTo(outOfGasExpected ? 0 : 1));
        }

        [TestCase("0x60006000556000600055", 1612, 0, 2301, 1)]
        [TestCase("0x60016000556001600055", 1612, 1, 2301, 1)]
        [TestCase("0x60006000556000600055", 1612, 0, 2299, 0)]
        [TestCase("0x60016000556001600055", 1612, 1, 2299, 0)]
        public void Test_when_gas_near_stipend(string codeHex, long gasUsed, byte originalValue, int stipendOffset, int expectedStatus)
        {
            SetupStorage(originalValue);

            TestAllTracerWithOutput receipt = Execute(BlockNumber, 21000 + gasUsed + (stipendOffset - 800), Bytes.FromHexString(codeHex));
            Assert.That(receipt.StatusCode, Is.EqualTo(expectedStatus));
        }

        private void SetupStorage(byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            TestState.Set(new StorageCell(Recipient, 0), new[] { originalValue });
            TestState.Commit(MainnetSpecProvider.Instance.GenesisSpec);
        }

        [Test]
        public void Eip8037_constants_are_calculated_correctly()
        {
            Assert.That(GasCostOf.CostPerStateByte, Is.EqualTo(1174));
            Assert.That(GasCostOf.SSetState, Is.EqualTo(37568));
            Assert.That(GasCostOf.CreateState, Is.EqualTo(131488));
            Assert.That(GasCostOf.NewAccountState, Is.EqualTo(131488));
            Assert.That(GasCostOf.PerAuthBaseState, Is.EqualTo(27002));
        }

        [TestCase(1, 6, 1174)]
        [TestCase(32, 6, 37568)]
        [TestCase(33, 12, 38742)]
        public void Eip8037_code_deposit_costs_are_split(int codeLength, long expectedRegular, long expectedState)
        {
            IReleaseSpec spec = Amsterdam.Instance;

            bool valid = CodeDepositHandler.CalculateCost(spec, codeLength, out long regularCost, out long stateCost);

            Assert.That(valid, Is.True);
            Assert.That(regularCost, Is.EqualTo(expectedRegular));
            Assert.That(stateCost, Is.EqualTo(expectedState));
        }

        [Test]
        public void Eip8037_state_gas_consumption_spills_to_regular_gas()
        {
            EthereumGasPolicy gas = new()
            {
                Value = 100,
                StateReservoir = 50,
                StateGasUsed = 0,
            };

            bool consumed = EthereumGasPolicy.ConsumeStateGas(ref gas, 70);

            Assert.That(consumed, Is.True);
            Assert.That(gas.StateReservoir, Is.EqualTo(0));
            Assert.That(gas.Value, Is.EqualTo(80));
            Assert.That(gas.StateGasUsed, Is.EqualTo(70));
        }

        [Test]
        public void Eip8037_child_frame_gets_full_state_reservoir()
        {
            EthereumGasPolicy parent = new()
            {
                Value = 1_000,
                StateReservoir = 333,
                StateGasUsed = 50,
            };

            EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);

            Assert.That(parent.Value, Is.EqualTo(1_000));
            Assert.That(parent.StateReservoir, Is.EqualTo(0));
            Assert.That(parent.StateGasUsed, Is.EqualTo(50));
            Assert.That(child.Value, Is.EqualTo(444));
            Assert.That(child.StateReservoir, Is.EqualTo(333));
            Assert.That(child.StateGasUsed, Is.EqualTo(0));
        }

        [Test]
        public void Eip8037_child_frame_refund_restores_remaining_state_reservoir()
        {
            EthereumGasPolicy parent = new()
            {
                Value = 1_000,
                StateReservoir = 333,
                StateGasUsed = 50,
            };

            EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);
            bool stateConsumed = EthereumGasPolicy.ConsumeStateGas(ref child, 100);
            bool regularConsumed = EthereumGasPolicy.UpdateGas(ref child, 150);

            Assert.That(stateConsumed, Is.True);
            Assert.That(regularConsumed, Is.True);

            EthereumGasPolicy.Refund(ref parent, in child);

            Assert.That(parent.Value, Is.EqualTo(1_294));
            Assert.That(parent.StateReservoir, Is.EqualTo(233));
            Assert.That(parent.StateGasUsed, Is.EqualTo(150));
        }

        [Test]
        public void Eip8037_state_refund_is_clamped_to_intrinsic_state_floor()
        {
            EthereumGasPolicy gas = new()
            {
                Value = 100,
                StateReservoir = 0,
                StateGasUsed = 120,
            };

            EthereumGasPolicy.RefundStateGas(ref gas, 200, stateGasFloor: 40);

            Assert.That(gas.StateReservoir, Is.EqualTo(200));
            Assert.That(gas.StateGasUsed, Is.EqualTo(0));
        }

        [Test]
        public void Eip8037_exceptional_halt_preserves_state_gas()
        {
            EthereumGasPolicy parent = new()
            {
                Value = 1_000,
                StateReservoir = 500,
                StateGasUsed = 10,
            };

            EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
            Assert.That(parent.StateReservoir, Is.EqualTo(0));

            // Child consumes some state gas
            EthereumGasPolicy.ConsumeStateGas(ref child, 200);
            Assert.That(child.StateReservoir, Is.EqualTo(300));
            Assert.That(child.StateGasUsed, Is.EqualTo(200));

            // Exceptional halt zeroes Value but preserves StateReservoir
            EthereumGasPolicy.SetOutOfGas(ref child);
            Assert.That(child.Value, Is.EqualTo(0));
            Assert.That(child.StateReservoir, Is.EqualTo(300));

            // Restore returns full original reservoir to parent
            EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 500);
            Assert.That(parent.StateReservoir, Is.EqualTo(500));
            Assert.That(parent.StateGasUsed, Is.EqualTo(10));
        }

        [Test]
        public void Eip8037_revert_restores_state_gas_to_parent_reservoir()
        {
            EthereumGasPolicy parent = new()
            {
                Value = 1_000,
                StateReservoir = 400,
                StateGasUsed = 20,
            };

            // Simulate parent allocating 600 regular gas to child (done by VM before CreateChildFrameGas)
            EthereumGasPolicy.Consume(ref parent, 600);
            EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);

            // Child uses some regular gas and state gas
            EthereumGasPolicy.UpdateGas(ref child, 100);
            EthereumGasPolicy.ConsumeStateGas(ref child, 150);

            // Simulate revert path: return remaining regular gas, restore state gas
            EthereumGasPolicy.UpdateGasUp(ref parent, EthereumGasPolicy.GetRemainingGas(in child));
            EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 400);

            // Parent gets remaining regular gas back
            Assert.That(parent.Value, Is.EqualTo(900));
            // Parent's reservoir is fully restored (child's remaining 250 + child's used 150 = 400)
            Assert.That(parent.StateReservoir, Is.EqualTo(400));
            // Parent's StateGasUsed is NOT merged with child's
            Assert.That(parent.StateGasUsed, Is.EqualTo(20));
        }

        [Test]
        public void Eip8037_spent_gas_subtracts_state_reservoir()
        {
            long gasLimit = 10_000;
            EthereumGasPolicy gas = new()
            {
                Value = 3_000,
                StateReservoir = 2_000,
                StateGasUsed = 500,
            };

            long spentGas = gasLimit
                - EthereumGasPolicy.GetRemainingGas(in gas)
                - EthereumGasPolicy.GetStateReservoir(in gas);

            Assert.That(spentGas, Is.EqualTo(5_000));
        }
    }
}
