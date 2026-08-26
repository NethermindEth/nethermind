// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
