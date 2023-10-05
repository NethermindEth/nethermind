// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing.ParityStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class ParityLikeTxTracerTests : VirtualMachineTestsBase
    {
        [Test]
        public void On_failure_result_is_null()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            Assert.IsNull(trace.Action.Result);
        }

        [Test]
        public void On_failure_block_and_tx_fields_are_set()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.From, Is.EqualTo(tx.SenderAddress), "from");
            Assert.That(trace.Action.To, Is.EqualTo(tx.To), "to");
            Assert.That(trace.BlockHash, Is.EqualTo(block.Hash), "hash");
            Assert.That(trace.BlockNumber, Is.EqualTo(block.Number), "number");
            Assert.That(trace.TransactionPosition, Is.EqualTo(0), "tx index");
            Assert.That(trace.TransactionHash, Is.EqualTo(tx.Hash), "tx hash");
            Assert.That(trace.Action.Gas, Is.EqualTo((long)tx.GasLimit - 21000), "gas");
            Assert.That(trace.Action.Value, Is.EqualTo(tx.Value), "value");
            Assert.That(trace.Action.Input, Is.EqualTo(tx.Data.AsArray()), "input");
            Assert.That(trace.Action.TraceAddress, Is.EqualTo(Array.Empty<int>()), "trace address");
        }

        [Test]
        public void Blockhash_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.BlockHash, Is.EqualTo(block.Hash));
        }

        [Test]
        [Todo(Improve.TestCoverage, "Add scenario where tx index is not 0")]
        public void Tx_index_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.TransactionPosition, Is.EqualTo(0));
        }

        [Test]
        public void Trace_address_is_valid_in_simple_cases()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.TraceAddress, Is.EqualTo(Array.Empty<int>()));
        }

        [Test]
        public void Tx_hash_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.TransactionHash, Is.EqualTo(tx.Hash));
        }

        [Test]
        public void Type_is_set_when_call()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.CallType, Is.EqualTo("call"));
        }

        [Test]
        public void Type_is_set_when_init()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteInitAndTraceParityCall(initCode);
            Assert.That(trace.Action.CallType, Is.EqualTo("create"));
        }

        [Test]
        public void Action_gas_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.Gas, Is.EqualTo(79000));
        }

        [Test]
        public void Action_call_type_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.CallType, Is.EqualTo("call"));
        }

        [Test]
        public void Action_from_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.From, Is.EqualTo(Sender));
        }

        [Test]
        public void Action_to_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.To, Is.EqualTo(Recipient));
        }

        [Test]
        public void Action_input_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            byte[] input = Bytes.FromHexString(SampleHexData2);
            UInt256 value = 1.Ether();

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(input, value, code);
            Assert.That(trace.Action.Input, Is.EqualTo(input));
        }

        [Test]
        public void Action_value_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            byte[] input = Bytes.FromHexString(SampleHexData2);
            UInt256 value = 1.Ether();

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(input, value, code);
            Assert.That(trace.Action.Value, Is.EqualTo(value));
        }

        [Test]
        public void Action_result_gas_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.Result.GasUsed, Is.EqualTo(3));
        }

        [Test]
        public void Action_result_output_is_set()
        {
            byte[] code = Prepare.EvmCode
                .StoreDataInMemory(0, SampleHexData1.PadLeft(64, '0'))
                .PushData("0x20")
                .PushData("0x0")
                .Op(Instruction.RETURN)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.Result.Output, Is.EqualTo(Bytes.FromHexString(SampleHexData1.PadLeft(64, '0'))));
        }

        [Test]
        public void Can_trace_nested_calls()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP
                1, // STOP
            };

            Assert.That(trace.Action.Subtraces.Count, Is.EqualTo(1), "root subtraces");
            Assert.That(trace.Action.Subtraces[0].Subtraces.Count, Is.EqualTo(1), "[0] subtraces");
            Assert.That(trace.Action.Subtraces[0].TraceAddress, Is.EqualTo(new[] { 0 }), "[0] address");
            Assert.That(trace.Action.Subtraces[0].Subtraces[0].Subtraces.Count, Is.EqualTo(0), "[0, 0] subtraces");
            Assert.That(trace.Action.Subtraces[0].Subtraces[0].TraceAddress, Is.EqualTo(new[] { 0, 0 }), "[0, 0] address");
        }

        [Test]
        public void Can_trace_delegate_calls()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .DelegateCall(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, // DELEGATE CALL
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP
                1, // STOP
            };

            Assert.That(trace.Action.Subtraces[0].CallType, Is.EqualTo("delegatecall"), "[0] type");
        }


        [Test]
        public void Can_trace_call_code_calls()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .CallCode(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL CODE
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP
                1, // STOP
            };

            Assert.That(trace.Action.Subtraces[0].CallType, Is.EqualTo("callcode"), "[0] type");
        }

        [Test]
        public void Can_trace_call_code_calls_with_large_data_offset()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .CallCode(TestItem.AddressC, 50000, UInt256.MaxValue, ulong.MaxValue)
                .Op(Instruction.STOP)
                .Done;

            ParityLikeTxTrace trace = ExecuteAndTraceParityCall(code).trace;
            trace.Action!.Error.Should().BeNullOrEmpty();

        }

        [Test]
        public void Can_trace_a_failing_static_call()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .CallWithValue(TestItem.AddressC, 50000, 1000000.Ether())
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);

            Assert.That(trace.Action.Subtraces.Count, Is.EqualTo(0), "subtraces count");
            Assert.That(trace.VmTrace.Operations.Last().Cost, Is.EqualTo(59700));
            Assert.That(trace.VmTrace.Operations.Last().Used, Is.EqualTo(71579));
        }

        [Test]
        public void Can_trace_memory_in_vm_trace()
        {
            string dataHex = "0x0102";
            string offsetHex = "0x01";

            byte[] code = Prepare.EvmCode
                .PushData(dataHex)
                .PushData(offsetHex)
                .Op(Instruction.MSTORE)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            ParityMemoryChangeTrace memory = trace.VmTrace.Operations[2].Memory;
            Assert.That(memory.Data.WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(dataHex));
            Assert.That(memory.Offset, Is.EqualTo(1));
        }

        [Test]
        public void Action_is_cleared_when_vm_trace_only()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(ParityTraceTypes.VmTrace, code);
            Assert.Null(trace.Action);
        }

        [Test]
        public void Can_trace_push_in_vm_trace()
        {
            string push1Hex = "0x01";
            string push2Hex = "0x0102";

            byte[] code = Prepare.EvmCode
                .PushData(push1Hex)
                .PushData(push2Hex)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            byte[][] push1 = trace.VmTrace.Operations[0].Push;
            byte[][] push2 = trace.VmTrace.Operations[1].Push;
            Assert.That(push1[0].WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push1Hex));
            Assert.That(push2[0].WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push2Hex));
        }

        [Test]
        public void Can_trace_dup_push_in_vm_trace()
        {
            string push1Hex = "0x01";
            string push2Hex = "0x0102";

            byte[] code = Prepare.EvmCode
                .PushData(push1Hex)
                .PushData(push2Hex)
                .Op(Instruction.DUP2)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            byte[][] dup = trace.VmTrace.Operations[2].Push;
            Assert.That(dup[0].WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push1Hex));
            Assert.That(dup[1].WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push2Hex));
        }

        [Test]
        public void Can_trace_swap_push_in_vm_trace()
        {
            string push1Hex = "0x01";
            string push2Hex = "0x0102";

            byte[] code = Prepare.EvmCode
                .PushData(push1Hex)
                .PushData(push2Hex)
                .Op(Instruction.SWAP1)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            byte[][] swap = trace.VmTrace.Operations[2].Push;
            Assert.That(swap[0].WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push2Hex));
            Assert.That(swap[1].WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push1Hex));
        }

        [Test]
        public void Can_trace_sstore_in_vm_trace()
        {
            string push1Hex = "0x01";
            string push2Hex = "0x0102";

            byte[] code = Prepare.EvmCode
                .PushData(push1Hex)
                .PushData(push2Hex)
                .Op(Instruction.SSTORE)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            ParityStorageChangeTrace sstore = trace.VmTrace.Operations[2].Store;
            Assert.That(sstore.Key.WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push2Hex));
            Assert.That(sstore.Value.WithoutLeadingZeros().ToArray().ToHexString(true), Is.EqualTo(push1Hex));
        }

        [Test]
        public void Can_trace_double_sstore()
        {
            string push1Hex = "0x01";
            string push2Hex = "0x0102";
            string push3Hex = "0x010203";

            byte[] code = Prepare.EvmCode
                .PushData(push2Hex)
                .PushData(push1Hex)
                .Op(Instruction.SSTORE)
                .PushData(push3Hex)
                .PushData(push1Hex)
                .Op(Instruction.SSTORE)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.StateChanges[TestItem.AddressB].Storage.Count, Is.EqualTo(1));
        }

        [Test]
        public void Can_trace_self_destruct()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.SELFDESTRUCT)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            Assert.That(trace.Action.Subtraces[0].Type, Is.EqualTo("suicide"));
        }

        [Test]
        public void Can_trace_failed_action()
        {
            string push1Hex = "0x01";
            string push2Hex = "0x0102";

            byte[] code = Prepare.EvmCode
                .PushData(push1Hex)
                .PushData(push2Hex)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code, 1000000.Ether());
            Assert.Null(trace.VmTrace);
            Assert.That(trace.Action.Value, Is.EqualTo(1000000.Ether()));
        }

        [Test]
        public void Can_trace_static_calls()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .StaticCall(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL CODE
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP
                1, // STOP
            };

            Assert.That(trace.Action.Subtraces[0].CallType, Is.EqualTo("staticcall"), "[0] type");
        }

        [Test]
        public void Can_trace_precompile_calls()
        {
            byte[] code = Prepare.EvmCode
                .Call(IdentityPrecompile.Instance.Address, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL
                2, // STOP
                1, // STOP
            };

            Assert.That(trace.Action.Subtraces[0].CallType, Is.EqualTo("call"), "[0] type");
            Assert.That(trace.Action.Subtraces[0].To, Is.EqualTo(IdentityPrecompile.Instance.Address), "[0] to");
        }

        [Test]
        public void Can_ignore_precompile_calls_in_contract()
        {
            byte[] deployedCode = Prepare.EvmCode
                .Call(IdentityPrecompile.Instance.Address, 50000)
                .CallWithValue(IdentityPrecompile.Instance.Address, 50000, 1.Ether())
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, deployedCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(IdentityPrecompile.Instance.Address, 50000)
                .Call(TestItem.AddressC, 40000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);

            // One call to precompile and the other call to AddressC
            Assert.That(trace.Action.Subtraces.Count, Is.EqualTo(2), "[] subtraces");
            Assert.That(trace.Action.CallType, Is.EqualTo("call"), "[] type");

            // Precompile call
            Assert.That(trace.Action.Subtraces[0].Subtraces.Count, Is.EqualTo(0), "[0] subtraces");
            Assert.That(trace.Action.Subtraces[0].CallType, Is.EqualTo("call"), "[0] type");
            Assert.That(trace.Action.Subtraces[0].To, Is.EqualTo(IdentityPrecompile.Instance.Address), "[0] to");

            // AddressC call - only one call
            Assert.That(trace.Action.Subtraces[1].Subtraces.Count, Is.EqualTo(2), "[1] subtraces");
            Assert.That(trace.Action.Subtraces[1].CallType, Is.EqualTo("call"), "[1] type");

            // Check the 1st subtrace - a precompile call
            Assert.That(trace.Action.Subtraces[1].Subtraces[0].Subtraces.Count, Is.EqualTo(0), "[1, 0] subtraces");
            Assert.That(trace.Action.Subtraces[1].Subtraces[0].CallType, Is.EqualTo("call"), "[1, 0] type");
            Assert.That(trace.Action.Subtraces[1].Subtraces[0].IncludeInTrace, Is.EqualTo(false), "[1, 0] type");

            // Check the 2nd subtrace - a precompile call with value - must be included
            Assert.That(trace.Action.Subtraces[1].Subtraces[1].Subtraces.Count, Is.EqualTo(0), "[1, 1] subtraces");
            Assert.That(trace.Action.Subtraces[1].Subtraces[1].CallType, Is.EqualTo("call"), "[1, 1] type");
            Assert.That(trace.Action.Subtraces[1].Subtraces[1].IncludeInTrace, Is.EqualTo(true), "[1, 1] type");
        }

        [Test]
        public void Can_trace_same_level_calls()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 40000)
                .Call(TestItem.AddressC, 40000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL
                3, 3, 3, 3, 3, 3, // CREATE
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP
                1, // STOP
            };

            Assert.That(trace.Action.Subtraces.Count, Is.EqualTo(2), "[] subtraces");
            Assert.That(trace.Action.CallType, Is.EqualTo("call"), "[] type");

            Assert.That(trace.Action.Subtraces[0].Subtraces.Count, Is.EqualTo(1), "[0] subtraces");
            Assert.That(trace.Action.Subtraces[0].TraceAddress, Is.EqualTo(new[] { 0 }), "[0] address");
            Assert.That(trace.Action.Subtraces[0].CallType, Is.EqualTo("call"), "[0] type");

            Assert.That(trace.Action.Subtraces[1].Subtraces.Count, Is.EqualTo(1), "[1] subtraces");
            Assert.That(trace.Action.Subtraces[1].TraceAddress, Is.EqualTo(new[] { 1 }), "[1] address");
            Assert.That(trace.Action.Subtraces[1].CallType, Is.EqualTo("call"), "[1] type");

            Assert.That(trace.Action.Subtraces[0].Subtraces[0].TraceAddress, Is.EqualTo(new[] { 0, 0 }), "[0, 0] address");
            Assert.That(trace.Action.Subtraces[0].Subtraces[0].Subtraces.Count, Is.EqualTo(0), "[0, 0] subtraces");
            Assert.That(trace.Action.Subtraces[1].Subtraces[0].CallType, Is.EqualTo("create"), "[0, 0] type");

            Assert.That(trace.Action.Subtraces[1].Subtraces[0].TraceAddress, Is.EqualTo(new[] { 1, 0 }), "[1, 0] address");
            Assert.That(trace.Action.Subtraces[1].Subtraces[0].Subtraces.Count, Is.EqualTo(0), "[1, 0] subtraces");
            Assert.That(trace.Action.Subtraces[1].Subtraces[0].CallType, Is.EqualTo("create"), "[1, 0] type");
        }

        [Test]
        public void Can_trace_storage_changes()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .PersistData("0x1", HexZero) // just to test if storage is restored
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .PersistData("0x2", SampleHexData1)
                .PersistData("0x3", SampleHexData2)
                .Call(TestItem.AddressC, 70000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);

            Assert.That(trace.StateChanges.Count, Is.EqualTo(5), "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(TestItem.AddressC), "address c");
            Assert.That(trace.StateChanges[Recipient].Storage.Count, Is.EqualTo(2), "recipient storage count");
            Assert.That(trace.StateChanges[Recipient].Storage[2].Before, Is.EqualTo(new byte[] { 0 }), "recipient storage[2]");
            Assert.That(trace.StateChanges[Recipient].Storage[2].After, Is.EqualTo(Bytes.FromHexString(SampleHexData1)), "recipient storage[2] after");
            Assert.That(trace.StateChanges[Recipient].Storage[3].Before, Is.EqualTo(new byte[] { 0 }), "recipient storage[3]");
            Assert.That(trace.StateChanges[Recipient].Storage[3].After, Is.EqualTo(Bytes.FromHexString(SampleHexData2)), "recipient storage[3] after");
        }

        [Test]
        public void Can_trace_code_changes()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .PersistData("0x1", HexZero) // just to test if storage is restored
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .PersistData("0x2", SampleHexData1)
                .PersistData("0x3", SampleHexData2)
                .Call(TestItem.AddressC, 70000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);

            Assert.That(trace.StateChanges.Count, Is.EqualTo(5), "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(TestItem.AddressC), "call target");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(Miner), "miner");
            Assert.That(trace.StateChanges[Contract].Code.Before, Is.EqualTo(null), "code before");
            Assert.That(trace.StateChanges[Contract].Code.After, Is.EqualTo(deployedCode), "code after");
        }

        [Test]
        public void Can_trace_balance_changes()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, Transaction tx) = ExecuteAndTraceParityCall(code);

            Assert.That(trace.StateChanges.Count, Is.EqualTo(3), "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Miner), "miner");
            Assert.That(trace.StateChanges[Sender].Balance.Before, Is.EqualTo(100.Ether()), "sender before");
            Assert.That(trace.StateChanges[Sender].Balance.After, Is.EqualTo(100.Ether() - 21001), "sender after");
            Assert.That(trace.StateChanges[Recipient].Balance.Before, Is.EqualTo(100.Ether()), "recipient before");
            Assert.That(trace.StateChanges[Recipient].Balance.After, Is.EqualTo(100.Ether() + 1), "recipient after");
            Assert.That(trace.StateChanges[Miner].Balance.Before, Is.EqualTo(null), "miner before");
            Assert.That(trace.StateChanges[Miner].Balance.After, Is.EqualTo((UInt256)21000), "miner after");
        }

        [Test]
        public void Can_trace_nonce_changes()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);

            Assert.That(trace.StateChanges.Count, Is.EqualTo(3), "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(Miner), "miner");
            Assert.That(trace.StateChanges[Sender].Nonce.Before, Is.EqualTo(UInt256.Zero), "sender before");
            Assert.That(trace.StateChanges[Sender].Nonce.After, Is.EqualTo(UInt256.One), "sender after");
        }

        [Test]
        public void Cannot_mark_as_failed_when_actions_stacked()
        {
            ParityLikeTxTracer tracer = new(Build.A.Block.TestObject, Build.A.Transaction.TestObject, ParityTraceTypes.All);
            tracer.ReportAction(1000L, 10, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Call, false);
            Assert.Throws<InvalidOperationException>(() => tracer.MarkAsFailed(TestItem.AddressA, 21000, Array.Empty<byte>(), "Error"));
        }

        [Test]
        public void Cannot_mark_as_success_when_actions_stacked()
        {
            ParityLikeTxTracer tracer = new(Build.A.Block.TestObject, Build.A.Transaction.TestObject, ParityTraceTypes.All);
            tracer.ReportAction(1000L, 10, Address.Zero, Address.Zero, Array.Empty<byte>(), ExecutionType.Call, false);
            Assert.Throws<InvalidOperationException>(() => tracer.MarkAsSuccess(TestItem.AddressA, 21000, Array.Empty<byte>(), new LogEntry[] { }));
        }

        [Test]
        public void Is_tracing_rewards_only_when_rewards_trace_type_selected()
        {
            ParityLikeBlockTracer tracer = new(ParityTraceTypes.All ^ ParityTraceTypes.Rewards);
            Assert.False(tracer.IsTracingRewards);

            ParityLikeBlockTracer tracer2 = new(ParityTraceTypes.Rewards);
            Assert.True(tracer2.IsTracingRewards);
        }

        private (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteInitAndTraceParityCall(params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareInitTx((BlockNumber, Timestamp), 100000, code);
            ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }

        private (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteAndTraceParityCall(params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code);
            ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff | ParityTraceTypes.VmTrace);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }

        private (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteAndTraceParityCall(ParityTraceTypes traceTypes, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code);
            ParityLikeTxTracer tracer = new(block, transaction, traceTypes);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }

        private (ParityLikeTxTrace trace, Block block, Transaction tx) ExecuteAndTraceParityCall(byte[] input, UInt256 value, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, input, value);
            ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.BuildResult(), block, transaction);
        }
    }
}
