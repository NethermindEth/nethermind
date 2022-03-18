//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationTracerTests : VirtualMachineTestsBase
    {
        
        [TestCase(Instruction.SELFDESTRUCT, 0, true)]
        [TestCase(Instruction.DELEGATECALL, 0, true)]
        [TestCase(Instruction.ADD, 0, true)]        
        [TestCase(Instruction.SELFDESTRUCT, 1, false)]
        [TestCase(Instruction.DELEGATECALL, 1, false)]
        [TestCase(Instruction.ADD, 1, true)]
        public void Bans_selfdestruct_and_delegatecall_only_when_paymaster_mode_is_active(Instruction instruction, int numberOpcodeCalls, bool success)
        {
            byte[] deployedCode = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x02")
                .PushData("0x03")
                .PushData("0x04")
                .PushData("0x05")
                .PushData("0x69")
                .Op(instruction)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak deployedCodeHash = TestState.UpdateCode(deployedCode);
            TestState.UpdateCodeHash(TestItem.AddressC, deployedCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                //.Op(Instruction.NUMBER)
                .Op(numberOpcodeCalls > 0 ? Instruction.NUMBER : Instruction.SELFBALANCE)
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);
            
            tracer.Success.Should().Be(success);
        }
        
        [TestCase(Instruction.GASPRICE, false)]
        [TestCase(Instruction.GASLIMIT, false)]
        [TestCase(Instruction.PREVRANDAO, false)]
        [TestCase(Instruction.TIMESTAMP, false)]
        [TestCase(Instruction.BASEFEE, false)]
        [TestCase(Instruction.BLOCKHASH, false)]
        [TestCase(Instruction.NUMBER, false)]
        [TestCase(Instruction.BALANCE, false)]
        [TestCase(Instruction.SELFBALANCE, false)]
        [TestCase(Instruction.ORIGIN, false)]
        [TestCase(Instruction.BALANCE, false)]
        [TestCase(Instruction.DUP1, true)]
        [TestCase(Instruction.ISZERO, true)]
        [TestCase(Instruction.AND, true)]
        public void Should_fail_if_banned_opcode_is_used_when_call_depth_is_more_than_one(Instruction instruction, bool success)
        {
            //GASPRICE, GASLIMIT, PREVRANDAO, TIMESTAMP, BASEFEE, BLOCKHASH, NUMBER, BALANCE, ORIGIN
            byte[] deployedCode = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x69")
                .Op(instruction)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak deployedCodeHash = TestState.UpdateCode(deployedCode);
            TestState.UpdateCodeHash(TestItem.AddressC, deployedCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);
            
            tracer.Success.Should().Be(success);
        }

        [TestCase(Instruction.NUMBER)]
        [TestCase(Instruction.GASPRICE)]
        public void Should_succeed_if_banned_opcode_is_used_with_calldepth_one(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x69")
                .PushData("0x01")
                .Op(instruction)
                .Done;

            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);
            
            tracer.Success.Should().BeTrue();
        }
        
        private (UserOperationTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceAccessCall(SenderRecipientAndMiner addresses, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
            UserOperationTxTracer tracer = new(transaction, TestState, TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, NullLogger.Instance);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer, block, transaction);
        }

        protected override long BlockNumber { get; } = MainnetSpecProvider.LondonBlockNumber;
    }
}
