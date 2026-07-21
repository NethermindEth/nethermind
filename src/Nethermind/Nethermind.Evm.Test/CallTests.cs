// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallTests : VirtualMachineTestsBase
    {
        protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Stack_underflow_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("StackUnderflow"));
        }

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Out_of_gas_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("OutOfGas"));
        }

        [TestCase(Instruction.CALL, 99, 1)]
        [TestCase(Instruction.CALLCODE, 100, 0)]
        [TestCase(Instruction.DELEGATECALL, 100, 0)]
        [TestCase(Instruction.STATICCALL, 100, 0)]
        public void Empty_code_call_preserves_balance_semantics(Instruction instruction, int expectedRecipientBalance, int expectedTargetBalance)
        {
            Address target = TestItem.AddressC;
            byte[] code = BuildEmptyCodeCall(instruction, target);
            (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code, value: 0);

            TransactionResult result = _processor.Execute(
                transaction,
                new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
                NullTxTracer.Instance);

            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(TestState.GetBalance(Recipient), Is.EqualTo((UInt256)expectedRecipientBalance * 1.Ether));
            Assert.That(TestState.GetBalance(target), Is.EqualTo((UInt256)expectedTargetBalance * 1.Ether));
        }

        [Test]
        public void Empty_code_staticcall_touches_existing_empty_account()
        {
            Address target = TestItem.AddressC;
            TestState.CreateAccount(target, UInt256.Zero);
            byte[] code = BuildEmptyCodeCall(Instruction.STATICCALL, target);
            (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code, value: 0);

            TransactionResult result = _processor.Execute(
                transaction,
                new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
                NullTxTracer.Instance);

            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(TestState.AccountExists(target), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Child_output_copy_preserves_memory_beyond_returned_bytes(bool revert)
        {
            Address target = TestItem.AddressC;
            Prepare childBuilder = Prepare.EvmCode
                .PushData("0x010203")
                .PushData(0)
                .Op(Instruction.MSTORE);
            byte[] childCode = (revert ? childBuilder.REVERT(29, 3) : childBuilder.RETURN(29, 3)).Done;
            TestState.CreateAccount(target, UInt256.Zero);
            TestState.InsertCode(target, childCode, SpecProvider.GenesisSpec);

            byte[] dirtyWord = Enumerable.Repeat((byte)0xff, EvmPooledMemory.WordSize).ToArray();
            byte[] parentCode = Prepare.EvmCode
                .MSTORE(0, dirtyWord)
                .CALL(50_000, target, 0, 0, 0, 0, 8)
                .Op(Instruction.POP)
                .RETURN(0, 8)
                .Done;

            TestAllTracerWithOutput tracer = Execute(parentCode);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.Error, Is.Null);
                Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[] { 1, 2, 3, 0xff, 0xff, 0xff, 0xff, 0xff }));
            }
        }

        private static byte[] BuildEmptyCodeCall(Instruction instruction, Address target) =>
            instruction switch
            {
                Instruction.CALL => Prepare.EvmCode.CallWithValue(target, 50_000, 1.Ether).Done,
                Instruction.CALLCODE => Prepare.EvmCode.CallCode(target, 50_000, 1.Ether).Done,
                Instruction.DELEGATECALL => Prepare.EvmCode.DelegateCall(target, 50_000).Done,
                Instruction.STATICCALL => Prepare.EvmCode.StaticCall(target, 50_000).Done,
                _ => throw new ArgumentOutOfRangeException(nameof(instruction), instruction, null)
            };
    }
}
