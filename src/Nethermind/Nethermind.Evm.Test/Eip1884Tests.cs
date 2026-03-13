// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Evm.State;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip1884Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        private TestAllTracerWithOutput Execute(bool isIstanbul, byte[] code) =>
            isIstanbul ? Execute(code) : Execute(BlockNumber - 1, 100000, code);

        [Test]
        public void after_istanbul_selfbalance_opcode_puts_current_address_balance_onto_the_stack()
        {
            byte[] contractCode = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether);
            TestState.InsertCode(TestItem.AddressC, contractCode, Spec);

            TestState.CreateAccount(TestItem.AddressD, 1.Ether);
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

        [TestCase(true, GasCostOf.ExtCodeHashEip1884)]
        [TestCase(false, GasCostOf.ExtCodeHash)]
        public void Extcodehash_cost_depends_on_istanbul(bool isIstanbul, long expectedOpGasCost)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether);

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;

            TestAllTracerWithOutput result = Execute(isIstanbul, code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + expectedOpGasCost);
        }

        [TestCase(true, GasCostOf.BalanceEip1884)]
        [TestCase(false, GasCostOf.BalanceEip150)]
        public void Balance_cost_depends_on_istanbul(bool isIstanbul, long expectedOpGasCost)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether);

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            TestAllTracerWithOutput result = Execute(isIstanbul, code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + expectedOpGasCost);
        }

        [TestCase(true, GasCostOf.SLoadEip1884)]
        [TestCase(false, GasCostOf.SLoadEip150)]
        public void Sload_cost_depends_on_istanbul(bool isIstanbul, long expectedOpGasCost)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether);

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.SLOAD)
                .Done;

            TestAllTracerWithOutput result = Execute(isIstanbul, code);
            AssertGas(result, 21000 + 2 * GasCostOf.VeryLow + expectedOpGasCost);
        }
    }
}
