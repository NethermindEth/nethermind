// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallTests : VirtualMachineTestsBase
    {
        private const string ValidECRecoverInput =
            "38d18acb67d25c8bb9942764b62f18e17054f66a817bd4295423adf9ed98873e" +
            "000000000000000000000000000000000000000000000000000000000000001b" +
            "38d18acb67d25c8bb9942764b62f18e17054f66a817bd4295423adf9ed98873e" +
            "789d1dd423d25f0772d2748d60f7e4b81bb14d086eba8e8e8efb6dcff8a4ae02";

        private static readonly byte[] ValidECRecoverOutput =
            Bytes.FromHexString("000000000000000000000000ceaccac640adf55b2028469bd36ba501f28b699d");

        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
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

        [Test]
        public void Staticcall_to_ecrecover_returns_success_with_empty_output_for_invalid_input()
        {
            TransactionResult result = ExecuteECRecoverStaticCall(Prepare.EvmCode, 128);

            Assert.That(result.TransactionExecuted, Is.True);
            AssertStorage(UInt256.Zero, UInt256.One);
            AssertStorage(UInt256.One, UInt256.Zero);
        }

        [Test]
        public void Staticcall_to_ecrecover_returns_success_with_empty_output_for_short_input()
        {
            TransactionResult result = ExecuteECRecoverStaticCall(Prepare.EvmCode, 63);

            Assert.That(result.TransactionExecuted, Is.True);
            AssertStorage(UInt256.Zero, UInt256.One);
            AssertStorage(UInt256.One, UInt256.Zero);
        }

        [Test]
        public void Staticcall_to_ecrecover_returns_success_with_empty_output_for_invalid_v()
        {
            byte[] invalidV = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000001");
            TransactionResult result = ExecuteECRecoverStaticCall(Prepare.EvmCode.MSTORE(32, invalidV), 128);

            Assert.That(result.TransactionExecuted, Is.True);
            AssertStorage(UInt256.Zero, UInt256.One);
            AssertStorage(UInt256.One, UInt256.Zero);
        }

        [Test]
        public void Staticcall_to_ecrecover_with_valid_v_uses_regular_precompile_result()
        {
            byte[] input = Bytes.FromHexString(ValidECRecoverInput);
            Prepare code = Prepare.EvmCode;
            for (int i = 0; i < 4; i++)
            {
                code.MSTORE((UInt256)(i * 32), input.AsSpan(i * 32, 32).ToArray());
            }

            TransactionResult result = ExecuteECRecoverStaticCall(code, 128);

            Assert.That(result.TransactionExecuted, Is.True);
            AssertStorage(UInt256.Zero, UInt256.One);
            AssertStorage(UInt256.One, ValidECRecoverOutput);
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

        private TransactionResult ExecuteECRecoverStaticCall(Prepare code, int dataLength)
        {
            byte[] bytecode = code
                .PushData(32)
                .PushData(128)
                .PushData(dataLength)
                .PushData(0)
                .PushData(PrecompiledAddresses.ECRecover)
                .PushData(50_000)
                .Op(Instruction.STATICCALL)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .PushData(128)
                .Op(Instruction.MLOAD)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .Done;

            (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, bytecode, value: 0);

            return _processor.Execute(
                transaction,
                new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
                NullTxTracer.Instance);
        }
    }
}
