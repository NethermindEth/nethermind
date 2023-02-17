// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;
using System;
using Nethermind.Int256;
using System.Linq;

namespace Nethermind.Evm.Test
{
    public class Eip5920Tests : VirtualMachineTestsBase
    {
        Nethermind.Core.Address zeroAddress = new Nethermind.Core.Address("0x0000000000000000000000000000000000000000");
        Nethermind.Core.Address address1 = new Nethermind.Core.Address("0x000000f000000000000000000000000000000000");
        Nethermind.Core.Address address2 = new Nethermind.Core.Address("0xf000001000000000000000000000000000000000");
        
        [Test]
        public void TestBurn()
        {
            // Prepare bytecode to burn 100 wei
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData(zeroAddress)
                .Op(Instruction.PAY)
                .Done;
            
            // Prepare address to have 150 wei
            TestState.CreateAccount(address1, 150);

            // Execute bytecode in the context of address1
            TestAllTracerWithOutput result = Execute(code, address1);

            // Validate
            result.Error.Should().BeNull();
            result.StatusCode.Should().Be(StatusCode.Success);
            TestState.GetBalance(address1).Should().Be(50);
            TestState.GetBalance(zeroAddress).Should().Be(0);
        }

        [Test]
        public void TestBurnInsufficientBalance()
        {
            // Prepare bytecode to burn 100 wei
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData(zeroAddress)
                .Op(Instruction.PAY)
                .Done;

            // Prepare address to have 70 wei
            TestState.CreateAccount(address1, 70);

            // Execute bytecode in the context of address1
            TestAllTracerWithOutput result = Execute(code, address1);

            // Validate
            result.Error.Should().NotBeNull();
            TestState.GetBalance(address1).Should().Be(70);
        }

        [Test]
        public void TestTransfer()
        {
            // Prepare bytecode to transfer 100 wei from address1 to address2
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData(address2)
                .Op(Instruction.PAY)
                .Done;

            // Prepare address1 to have 150 wei and address2 to have 20 wei
            TestState.CreateAccount(address1, 150);
            TestState.CreateAccount(address2, 20);

            // Execute bytecode in the context of address1
            TestAllTracerWithOutput result = Execute(code, address1);

            // Validate
            result.Error.Should().BeNull();
            result.StatusCode.Should().Be(StatusCode.Success);
            TestState.GetBalance(address1).Should().Be(50);
            TestState.GetBalance(address2).Should().Be(120);
        }

        [Test]
        public void TestTransferInsufficientBalance()
        {
            // Prepare bytecode to transfer 100 wei from address1 to address2
            byte[] code = Prepare.EvmCode
                .PushData(100)
                .PushData(address2)
                .Op(Instruction.PAY)
                .Done;

            // Prepare address1 to have 70 wei and address2 to have 20 wei
            TestState.CreateAccount(address1, 70);
            TestState.CreateAccount(address2, 20);

            // Execute bytecode in the context of address1
            TestAllTracerWithOutput result = Execute(code, address1);

            // Validate
            result.Error.Should().NotBeNull();
            TestState.GetBalance(address1).Should().Be(70);
            TestState.GetBalance(address2).Should().Be(20);
        }
    }
}
