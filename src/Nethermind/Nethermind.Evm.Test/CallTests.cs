// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
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
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDisposed")]
        private static extern ref bool GetIsDisposed(VmState<EthereumGasPolicy> state);

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

        [TestCase(Instruction.INVALID)]
        [TestCase(Instruction.REVERT)]
        public void Nested_halt_preserves_ripemd_empty_account_deletion(Instruction halt)
        {
            Address child = TestItem.AddressC;
            TestState.CreateAccount(child, UInt256.Zero);
            byte[] childCode = BuildRipemdTouchThenHalt(halt);
            TestState.InsertCode(child, childCode, SpecProvider.GenesisSpec);
            byte[] code = Prepare.EvmCode
                .Call(child, 150_000)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done;
            AssertRipemdTouchPreserved(code, (MainnetSpecProvider.ByzantiumBlockNumber, 0), 300_000);
        }

        [TestCase(Instruction.INVALID)]
        [TestCase(Instruction.REVERT)]
        public void Top_level_halt_preserves_ripemd_empty_account_deletion(Instruction halt)
        {
            byte[] code = BuildRipemdTouchThenHalt(halt);
            AssertRipemdTouchPreserved(code, (MainnetSpecProvider.ByzantiumBlockNumber, 0), 300_000);
        }

        [Test]
        public void Failed_nested_code_deposit_preserves_ripemd_empty_account_deletion()
        {
            byte[] initCode = BuildRipemdTouchThenReturnInvalidCode();
            byte[] code = Prepare.EvmCode
                .Create(initCode, UInt256.Zero)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done;
            AssertRipemdTouchPreserved(code, (MainnetSpecProvider.LondonBlockNumber, 0), 500_000);
        }

        [Test]
        public void Failed_top_level_code_deposit_preserves_ripemd_empty_account_deletion()
        {
            byte[] initCode = BuildRipemdTouchThenReturnInvalidCode();
            AssertRipemdTouchPreserved(initCode, (MainnetSpecProvider.LondonBlockNumber, 0), 500_000, contractCreation: true);
        }

        private void AssertRipemdTouchPreserved(
            byte[] code,
            ForkActivation activation,
            ulong gasLimit,
            bool contractCreation = false)
        {
            TestState.CreateAccount(Ripemd160Precompile.Address, UInt256.Zero);
            (Block block, Transaction transaction) = PrepareTx(activation, gasLimit, code, value: 0);
            if (contractCreation)
            {
                transaction.To = null;
                transaction.Data = code;
            }

            TransactionResult result = _processor.Execute(
                transaction,
                new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
                NullTxTracer.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TransactionExecuted, Is.True);
                Assert.That(TestState.AccountExists(Ripemd160Precompile.Address), Is.False);
            }
        }

        private static byte[] BuildRipemdTouchThenHalt(Instruction halt)
        {
            Prepare code = Prepare.EvmCode
                .Call(Ripemd160Precompile.Address, 50_000)
                .Op(Instruction.POP)
                .Call(BN254PairingCheckPrecompile.Address, 0)
                .Op(Instruction.POP);

            return halt switch
            {
                Instruction.INVALID => code.Op(Instruction.INVALID).Done,
                Instruction.REVERT => code.Revert(0, 0).Done,
                _ => throw new ArgumentOutOfRangeException(nameof(halt), halt, null),
            };
        }

        private static byte[] BuildRipemdTouchThenReturnInvalidCode() => Prepare.EvmCode
            .Call(Ripemd160Precompile.Address, 50_000)
            .Op(Instruction.POP)
            .PushData(0xef)
            .PushData(0)
            .Op(Instruction.MSTORE8)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        [Test]
        public void Child_output_copy_preserves_memory_beyond_returned_bytes(
            [Values] bool revert, [Values(0, 31, 1023, 1024)] int outputOffset,
            [Values(0, 1, 8, 64)] int requestedLength, [Values] bool traced)
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
                .MSTORE((UInt256)outputOffset, dirtyWord)
                .CALL(50_000, target, 0, 0, 0, (UInt256)outputOffset, (UInt256)requestedLength)
                .Op(Instruction.POP)
                .RETURN((UInt256)outputOffset, 8)
                .Done;

            TestAllTracerWithOutput tracer = new OutputCopyTracer(traced);
            Execute(tracer, parentCode);
            byte[] expected = Enumerable.Repeat((byte)0xff, 8).ToArray();
            for (int i = 0; i < Math.Min(3, requestedLength); i++) expected[i] = (byte)(i + 1);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.Error, Is.Null);
                Assert.That(tracer.ReturnValue, Is.EqualTo(expected));
            }
        }

        private sealed class OutputCopyTracer(bool traced) : TestAllTracerWithOutput
        {
            public override bool IsTracingInstructions => traced;
        }

        [Test]
        public void Child_receives_input_across_memory_boundaries(
            [Values(0, 31, 1023, 1024)] int inputOffset,
            [Values(0, 1, 32, 64)] int inputLength, [Values] bool traced)
        {
            byte[] data = new byte[64];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
            byte[] childCode = Prepare.EvmCode
                .Op(Instruction.CALLDATASIZE).PushData(0).PushData(0).Op(Instruction.CALLDATACOPY)
                .Op(Instruction.CALLDATASIZE).PushData(0).Op(Instruction.RETURN).Done;
            TestState.CreateAccount(TestItem.AddressC, UInt256.Zero);
            TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);
            byte[] parentCode = Prepare.EvmCode.StoreDataInMemory(inputOffset, data)
                .CALL(50_000, TestItem.AddressC, 0, (UInt256)inputOffset, (UInt256)inputLength, 2048, (UInt256)inputLength)
                .Op(Instruction.POP).RETURN(2048, (UInt256)inputLength).Done;
            AssertSuccessfulOutput(parentCode, data.AsSpan(0, inputLength).ToArray(), traced);
        }

        [Test]
        public void Resumed_call_pushes_status_after_a_full_stack(
            [Values(Instruction.CALL, Instruction.CALLCODE, Instruction.DELEGATECALL, Instruction.STATICCALL)] Instruction instruction,
            [Values] bool revert, [Values] bool traced)
        {
            byte[] childCode = Prepare.EvmCode.PushData(0).PushData(0)
                .Op(revert ? Instruction.REVERT : Instruction.RETURN).Done;
            TestState.CreateAccount(TestItem.AddressC, UInt256.Zero);
            TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);
            bool hasValue = instruction is Instruction.CALL or Instruction.CALLCODE;
            Prepare parent = Prepare.EvmCode;
            for (int i = 0; i < 1024 - (hasValue ? 7 : 6); i++) parent.Op(Instruction.PUSH0);
            parent.PushData(0).PushData(0).PushData(0).PushData(0);
            if (hasValue) parent.PushData(0);
            byte[] code = parent.PushData(TestItem.AddressC).PushData(50_000).Op(instruction)
                .PushData(0).Op(Instruction.MSTORE).RETURN(0, 32).Done;
            AssertSuccessfulOutput(code, ((UInt256)(revert ? 0 : 1)).ToBigEndian(), traced);
        }

        [Test]
        public void Resumed_create_pushes_result_after_a_full_stack(
            [Values(Instruction.CREATE, Instruction.CREATE2)] Instruction instruction,
            [Values] bool revert, [Values] bool traced)
        {
            byte[] initCode = Prepare.EvmCode.PushData(0).PushData(0)
                .Op(revert ? Instruction.REVERT : Instruction.RETURN).Done;
            Prepare parent = Prepare.EvmCode.StoreDataInMemory(0, initCode);
            for (int i = 0; i < (instruction == Instruction.CREATE2 ? 1020 : 1021); i++) parent.Op(Instruction.PUSH0);
            if (instruction == Instruction.CREATE2) parent.Op(Instruction.PUSH0);
            byte[] code = parent.PushData(initCode.Length).PushData(0).PushData(0).Op(instruction)
                .PushData(0).Op(Instruction.MSTORE).RETURN(0, 32).Done;
            byte[] expected = new byte[32];
            if (!revert)
            {
                Address address = instruction == Instruction.CREATE2
                    ? ContractAddress.From(Recipient, new byte[32], initCode)
                    : ContractAddress.From(Recipient, 0);
                address.Bytes.CopyTo(expected.AsSpan(12));
            }
            AssertSuccessfulOutput(code, expected, traced);
        }

        private void AssertSuccessfulOutput(byte[] code, byte[] expected, bool traced)
        {
            TestAllTracerWithOutput tracer = new OutputCopyTracer(traced);
            Execute(tracer, code);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.Error, Is.Null);
                Assert.That(tracer.ReturnValue, Is.EqualTo(expected));
            }
        }

        [Test]
        public void Create_init_code_survives_reverted_frame_reuse()
        {
            byte[] initCode = Prepare.EvmCode.Revert(0, 0).Done;
            byte[] createCode = Prepare.EvmCode
                .Create(initCode, UInt256.Zero)
                .Op(Instruction.STOP)
                .Done;
            RetainedCreateInputTracer tracer = new(Machine);

            Execute(tracer, createCode);
            Assert.That(tracer.CreateInput.ToArray(), Is.EqualTo(initCode));

            byte[] replacement = new byte[EvmPooledMemory.WordSize];
            Array.Fill(replacement, (byte)0xa5);
            byte[] overwriteCode = Prepare.EvmCode
                .StoreDataInMemory(0, replacement)
                .Op(Instruction.STOP)
                .Done;
            Execute(tracer, overwriteCode);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracer.CreateReverted, Is.True);
                Assert.That(tracer.SecondTopLevelState, Is.SameAs(tracer.FirstTopLevelState));
                Assert.That(tracer.CreateInput.ToArray(), Is.EqualTo(initCode));
            }
        }

        [Test]
        public void Cancellation_in_nested_frame_disposes_active_frames()
        {
            Address middle = TestItem.AddressC;
            Address leaf = TestItem.AddressD;
            TestState.CreateAccount(middle, UInt256.Zero);
            TestState.CreateAccount(leaf, UInt256.Zero);
            byte[] leafCode = [(byte)Instruction.STOP];
            TestState.InsertCode(leaf, leafCode, SpecProvider.GenesisSpec);
            byte[] middleCode = Prepare.EvmCode
                .CALL(50_000, leaf, 0, 0, 0, 0, 0)
                .Op(Instruction.STOP)
                .Done;
            TestState.InsertCode(middle, middleCode, SpecProvider.GenesisSpec);
            byte[] code = Prepare.EvmCode
                .CALL(50_000, middle, 0, 0, 0, 0, 0)
                .Op(Instruction.STOP)
                .Done;
            (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code, value: 0);
            CancelOnNestedFrameTracer tracer = new(Machine);

            Assert.Throws<OperationCanceledException>(() =>
                _processor.Execute(
                    transaction,
                    new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
                    tracer));

            Assert.That(tracer.ParentState, Is.Not.Null);
            Assert.That(tracer.IntermediateState, Is.Not.Null);
            Assert.That(tracer.CancelledState, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetStateStack().Count, Is.Zero);
                Assert.That(tracer.IntermediateState, Is.Not.SameAs(tracer.ParentState));
                Assert.That(tracer.IntermediateState, Is.Not.SameAs(tracer.CancelledState));
                Assert.That(GetIsDisposed(tracer.ParentState!), Is.True);
                Assert.That(GetIsDisposed(tracer.IntermediateState!), Is.True);
                Assert.That(GetIsDisposed(tracer.CancelledState!), Is.True);
            }
        }

        private VmStateStack<EthereumGasPolicy> GetStateStack() =>
            (VmStateStack<EthereumGasPolicy>)typeof(VirtualMachine<EthereumGasPolicy>)
                .GetField("_stateStack", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Machine)!;

        private sealed class CancelOnNestedFrameTracer(EthereumVirtualMachine machine) : TestAllTracerWithOutput, ITxTracer
        {
            private int _pollCount;

            public VmState<EthereumGasPolicy>? ParentState { get; private set; }
            public VmState<EthereumGasPolicy>? IntermediateState { get; private set; }
            public VmState<EthereumGasPolicy>? CancelledState { get; private set; }

            bool ITxTracer.IsCancelable => true;

            bool ITxTracer.IsCancelled
            {
                get
                {
                    if (++_pollCount == 1)
                    {
                        ParentState = machine.VmState;
                        return false;
                    }

                    if (_pollCount == 2)
                    {
                        IntermediateState = machine.VmState;
                        return false;
                    }

                    CancelledState = machine.VmState;
                    return true;
                }
            }
        }

        private sealed class RetainedCreateInputTracer(EthereumVirtualMachine machine) : TestAllTracerWithOutput
        {
            private int _transactionCount;

            public ReadOnlyMemory<byte> CreateInput { get; private set; }
            public bool CreateReverted { get; private set; }
            public VmState<EthereumGasPolicy>? FirstTopLevelState { get; private set; }
            public VmState<EthereumGasPolicy>? SecondTopLevelState { get; private set; }

            public override void ReportAction(ulong gas, UInt256 value, Address from, Address to,
                ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            {
                base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

                if (callType == ExecutionType.TRANSACTION)
                {
                    if (_transactionCount++ == 0)
                        FirstTopLevelState = machine.VmState;
                    else
                        SecondTopLevelState = machine.VmState;
                }
                else if (callType == ExecutionType.CREATE)
                {
                    CreateInput = input;
                }
            }

            public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
            {
                base.ReportActionRevert(gas, output);
                CreateReverted = true;
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
