// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip5920Tests : VirtualMachineTestsBase
    {
        [Test]
        public void TestBurn()
        {
            // Prepare bytecode to burn 100 wei
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData("0x0000000000000000000000000000000000000000")
                .Op(Instruction.PAY)
                .Done;
            
            // Prepare address to have 150 wei
            TestState.CreateAccount(TestItem.AddressF, 150.Ether());

            // Execute bytecode in the context of addressF
            TestAllTracerWithOutput result = Execute(code, TestItem.AddressF);

            // Validate
            result.Error.Should().BeNull();
            result.StatusCode.Should().Be(1);
            TestState.GetBalance(TestItem.AddressF).Should().Be(50);
            TestState.GetBalance("0x0000000000000000000000000000000000000000").Should().Be(0);
        }

        [Test]
        public void TestBurnInsufficientBalance()
        {
            // Prepare bytecode to burn 100 wei
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData("0x0000000000000000000000000000000000000000")
                .Op(Instruction.PAY)
                .Done;

            // Prepare address to have 70 wei
            TestState.CreateAccount(TestItem.AddressF, 70.Ether());

            // Execute bytecode in the context of addressF
            TestAllTracerWithOutput result = Execute(code, TestItem.AddressF);

            // Validate
            result.Error.Should().NotBeNull();
        }

        [Test]
        public void TestTransfer()
        {
            // Prepare bytecode to transfer 100 wei from addressF to addressG
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData(TestItem.AddressG)
                .Op(Instruction.PAY)
                .Done;

            // Prepare addressF to have 150 wei and addressG to have 20 wei
            TestState.CreateAccount(TestItem.AddressF, 150.Ether());
            TestState.CreateAccount(TestItem.AddressG, 20.Ether());

            // Execute bytecode in the context of addressF
            TestAllTracerWithOutput result = Execute(code, TestItem.AddressF);

            // Validate
            result.Error.Should().BeNull();
            result.StatusCode.Should().Be(1);
            TestState.GetBalance(TestItem.AddressF).Should().Be(50);
            TestState.GetBalance(TestItem.AddressG).Should().Be(120);
        }

        [Test]
        public void TestTransferInsufficientBalance()
        {
            // Prepare bytecode to transfer 100 wei from addressF to addressG
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData(TestItem.AddressG)
                .Op(Instruction.PAY)
                .Done;

            // Prepare addressF to have 70 wei and addressG to have 20 wei
            TestState.CreateAccount(TestItem.AddressF, 70.Ether());
            TestState.CreateAccount(TestItem.AddressG, 20.Ether());

            // Execute bytecode in the context of addressF
            TestAllTracerWithOutput result = Execute(code, TestItem.AddressF);

            // Validate
            result.Error.Should().NotBeNull();
        }
    }
}
