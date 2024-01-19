// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip1884Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [Test]
        public void after_istanbul_selfbalance_opcode_puts_current_address_balance_onto_the_stack()
        {
            byte[] contractCode = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, contractCode, Spec);

            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            TestState.InsertCode(TestItem.AddressD, contractCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .DelegateCall(TestItem.AddressD, 50000)
                .Op(Instruction.SELFBALANCE)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertGas(result, 21000 + 2 * GasCostOf.CallEip150 + 24 + 21 + GasCostOf.VeryLow + 3 * GasCostOf.SelfBalance + 3 * GasCostOf.SSet);
            UInt256 balanceB = TestState.GetBalance(TestItem.AddressB);
            UInt256 balanceC = TestState.GetBalance(TestItem.AddressC);
            AssertStorage(new StorageCell(TestItem.AddressB, UInt256.Zero), balanceB);
            AssertStorage(new StorageCell(TestItem.AddressB, UInt256.One), balanceB);
            AssertStorage(new StorageCell(TestItem.AddressC, UInt256.Zero), balanceC);
        }

        [Test]
        public void after_istanbul_extcodehash_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.ExtCodeHashEip1884);
        }

        [Test]
        public void after_istanbul_balance_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.BalanceEip1884);
        }

        [Test]
        public void after_istanbul_sload_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.SLOAD)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            AssertGas(result, 21000 + 2 * GasCostOf.VeryLow + GasCostOf.SLoadEip1884);
        }

        [Test]
        public void just_before_istanbul_extcodehash_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;

            TestAllTracerWithOutput result = Execute(BlockNumber - 1, 100000, code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.ExtCodeHash);
        }

        [Test]
        public void just_before_istanbul_balance_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            TestAllTracerWithOutput result = Execute(BlockNumber - 1, 100000, code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.BalanceEip150);
        }

        [Test]
        public void just_before_istanbul_sload_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.SLOAD)
                .Done;

            TestAllTracerWithOutput result = Execute(BlockNumber - 1, 100000, code);
            AssertGas(result, 21000 + 2 * GasCostOf.VeryLow + GasCostOf.SLoadEip150);
        }
    }
}
