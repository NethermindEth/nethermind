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
            Assert.AreEqual(tx.SenderAddress, trace.Action.From, "from");
            Assert.AreEqual(tx.To, trace.Action.To, "to");
            Assert.AreEqual(block.Hash, trace.BlockHash, "hash");
            Assert.AreEqual(block.Number, trace.BlockNumber, "number");
            Assert.AreEqual(0, trace.TransactionPosition, "tx index");
            Assert.AreEqual(tx.Hash, trace.TransactionHash, "tx hash");
            Assert.AreEqual((long)tx.GasLimit - 21000, trace.Action.Gas, "gas");
            Assert.AreEqual(tx.Value, trace.Action.Value, "value");
            Assert.AreEqual(tx.Data, trace.Action.Input, "input");
            Assert.AreEqual(Array.Empty<int>(), trace.Action.TraceAddress, "trace address");
        }

        [Test]
        public void Blockhash_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(block.Hash, trace.BlockHash);
        }

        [Test]
        [Todo(Improve.TestCoverage, "Add scenario where tx index is not 0")]
        public void Tx_index_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(0, trace.TransactionPosition);
        }

        [Test]
        public void Trace_address_is_valid_in_simple_cases()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Array.Empty<int>(), trace.Action.TraceAddress);
        }

        [Test]
        public void Tx_hash_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(tx.Hash, trace.TransactionHash);
        }

        [Test]
        public void Type_is_set_when_call()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual("call", trace.Action.CallType);
        }

        [Test]
        public void Type_is_set_when_init()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteInitAndTraceParityCall(initCode);
            Assert.AreEqual("create", trace.Action.CallType);
        }

        [Test]
        public void Action_gas_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(79000, trace.Action.Gas);
        }

        [Test]
        public void Action_call_type_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual("call", trace.Action.CallType);
        }

        [Test]
        public void Action_from_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Sender, trace.Action.From);
        }

        [Test]
        public void Action_to_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Recipient, trace.Action.To);
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
            Assert.AreEqual(input, trace.Action.Input);
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
            Assert.AreEqual(value, trace.Action.Value);
        }

        [Test]
        public void Action_result_gas_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(3, trace.Action.Result.GasUsed);
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
            Assert.AreEqual(Bytes.FromHexString(SampleHexData1.PadLeft(64, '0')), trace.Action.Result.Output);
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

            Assert.AreEqual(1, trace.Action.Subtraces.Count, "root subtraces");
            Assert.AreEqual(1, trace.Action.Subtraces[0].Subtraces.Count, "[0] subtraces");
            Assert.AreEqual(new[] { 0 }, trace.Action.Subtraces[0].TraceAddress, "[0] address");
            Assert.AreEqual(0, trace.Action.Subtraces[0].Subtraces[0].Subtraces.Count, "[0, 0] subtraces");
            Assert.AreEqual(new[] { 0, 0 }, trace.Action.Subtraces[0].Subtraces[0].TraceAddress, "[0, 0] address");
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

            Assert.AreEqual("delegatecall", trace.Action.Subtraces[0].CallType, "[0] type");
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

            Assert.AreEqual("callcode", trace.Action.Subtraces[0].CallType, "[0] type");
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

            Assert.AreEqual(0, trace.Action.Subtraces.Count, "subtraces count");
            Assert.AreEqual(59700, trace.VmTrace.Operations.Last().Cost);
            Assert.AreEqual(71579, trace.VmTrace.Operations.Last().Used);
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
            Assert.AreEqual(dataHex, memory.Data.WithoutLeadingZeros().ToArray().ToHexString(true));
            Assert.AreEqual(1, memory.Offset);
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
            Assert.AreEqual(push1Hex, push1[0].WithoutLeadingZeros().ToArray().ToHexString(true));
            Assert.AreEqual(push2Hex, push2[0].WithoutLeadingZeros().ToArray().ToHexString(true));
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
            Assert.AreEqual(push1Hex, dup[0].WithoutLeadingZeros().ToArray().ToHexString(true));
            Assert.AreEqual(push2Hex, dup[1].WithoutLeadingZeros().ToArray().ToHexString(true));
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
            Assert.AreEqual(push2Hex, swap[0].WithoutLeadingZeros().ToArray().ToHexString(true));
            Assert.AreEqual(push1Hex, swap[1].WithoutLeadingZeros().ToArray().ToHexString(true));
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
            Assert.AreEqual(push2Hex, sstore.Key.WithoutLeadingZeros().ToArray().ToHexString(true));
            Assert.AreEqual(push1Hex, sstore.Value.WithoutLeadingZeros().ToArray().ToHexString(true));
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
            Assert.AreEqual(1, trace.StateChanges[TestItem.AddressB].Storage.Count);
        }

        [Test]
        public void Can_trace_self_destruct()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.SELFDESTRUCT)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual("suicide", trace.Action.Subtraces[0].Type);
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
            Assert.AreEqual(1000000.Ether(), trace.Action.Value);
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

            Assert.AreEqual("staticcall", trace.Action.Subtraces[0].CallType, "[0] type");
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

            Assert.AreEqual("call", trace.Action.Subtraces[0].CallType, "[0] type");
            Assert.AreEqual(IdentityPrecompile.Instance.Address, trace.Action.Subtraces[0].To, "[0] to");
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
            Assert.AreEqual(2, trace.Action.Subtraces.Count, "[] subtraces");
            Assert.AreEqual("call", trace.Action.CallType, "[] type");

            // Precompile call
            Assert.AreEqual(0, trace.Action.Subtraces[0].Subtraces.Count, "[0] subtraces");
            Assert.AreEqual("call", trace.Action.Subtraces[0].CallType, "[0] type");
            Assert.AreEqual(IdentityPrecompile.Instance.Address, trace.Action.Subtraces[0].To, "[0] to");

            // AddressC call - only one call
            Assert.AreEqual(2, trace.Action.Subtraces[1].Subtraces.Count, "[1] subtraces");
            Assert.AreEqual("call", trace.Action.Subtraces[1].CallType, "[1] type");

            // Check the 1st subtrace - a precompile call
            Assert.AreEqual(0, trace.Action.Subtraces[1].Subtraces[0].Subtraces.Count, "[1, 0] subtraces");
            Assert.AreEqual("call", trace.Action.Subtraces[1].Subtraces[0].CallType, "[1, 0] type");
            Assert.AreEqual(false, trace.Action.Subtraces[1].Subtraces[0].IncludeInTrace, "[1, 0] type");

            // Check the 2nd subtrace - a precompile call with value - must be included
            Assert.AreEqual(0, trace.Action.Subtraces[1].Subtraces[1].Subtraces.Count, "[1, 1] subtraces");
            Assert.AreEqual("call", trace.Action.Subtraces[1].Subtraces[1].CallType, "[1, 1] type");
            Assert.AreEqual(true, trace.Action.Subtraces[1].Subtraces[1].IncludeInTrace, "[1, 1] type");
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

            Assert.AreEqual(2, trace.Action.Subtraces.Count, "[] subtraces");
            Assert.AreEqual("call", trace.Action.CallType, "[] type");

            Assert.AreEqual(1, trace.Action.Subtraces[0].Subtraces.Count, "[0] subtraces");
            Assert.AreEqual(new[] { 0 }, trace.Action.Subtraces[0].TraceAddress, "[0] address");
            Assert.AreEqual("call", trace.Action.Subtraces[0].CallType, "[0] type");

            Assert.AreEqual(1, trace.Action.Subtraces[1].Subtraces.Count, "[1] subtraces");
            Assert.AreEqual(new[] { 1 }, trace.Action.Subtraces[1].TraceAddress, "[1] address");
            Assert.AreEqual("call", trace.Action.Subtraces[1].CallType, "[1] type");

            Assert.AreEqual(new[] { 0, 0 }, trace.Action.Subtraces[0].Subtraces[0].TraceAddress, "[0, 0] address");
            Assert.AreEqual(0, trace.Action.Subtraces[0].Subtraces[0].Subtraces.Count, "[0, 0] subtraces");
            Assert.AreEqual("create", trace.Action.Subtraces[1].Subtraces[0].CallType, "[0, 0] type");

            Assert.AreEqual(new[] { 1, 0 }, trace.Action.Subtraces[1].Subtraces[0].TraceAddress, "[1, 0] address");
            Assert.AreEqual(0, trace.Action.Subtraces[1].Subtraces[0].Subtraces.Count, "[1, 0] subtraces");
            Assert.AreEqual("create", trace.Action.Subtraces[1].Subtraces[0].CallType, "[1, 0] type");
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

            Assert.AreEqual(5, trace.StateChanges.Count, "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(TestItem.AddressC), "address c");
            Assert.AreEqual(2, trace.StateChanges[Recipient].Storage.Count, "recipient storage count");
            Assert.AreEqual(new byte[] { 0 }, trace.StateChanges[Recipient].Storage[2].Before, "recipient storage[2]");
            Assert.AreEqual(Bytes.FromHexString(SampleHexData1), trace.StateChanges[Recipient].Storage[2].After, "recipient storage[2] after");
            Assert.AreEqual(new byte[] { 0 }, trace.StateChanges[Recipient].Storage[3].Before, "recipient storage[3]");
            Assert.AreEqual(Bytes.FromHexString(SampleHexData2), trace.StateChanges[Recipient].Storage[3].After, "recipient storage[3] after");
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

            Assert.AreEqual(5, trace.StateChanges.Count, "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(TestItem.AddressC), "call target");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(Miner), "miner");
            Assert.AreEqual(null, trace.StateChanges[Contract].Code.Before, "code before");
            Assert.AreEqual(deployedCode, trace.StateChanges[Contract].Code.After, "code after");
        }

        [Test]
        public void Can_trace_balance_changes()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, Transaction tx) = ExecuteAndTraceParityCall(code);

            Assert.AreEqual(3, trace.StateChanges.Count, "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Miner), "miner");
            Assert.AreEqual(100.Ether(), trace.StateChanges[Sender].Balance.Before, "sender before");
            Assert.AreEqual(100.Ether() - 21001, trace.StateChanges[Sender].Balance.After, "sender after");
            Assert.AreEqual(100.Ether(), trace.StateChanges[Recipient].Balance.Before, "recipient before");
            Assert.AreEqual(100.Ether() + 1, trace.StateChanges[Recipient].Balance.After, "recipient after");
            Assert.AreEqual(null, trace.StateChanges[Miner].Balance.Before, "miner before");
            Assert.AreEqual((UInt256)21000, trace.StateChanges[Miner].Balance.After, "miner after");
        }

        [Test]
        public void Can_trace_nonce_changes()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeTxTrace trace, _, _) = ExecuteAndTraceParityCall(code);

            Assert.AreEqual(3, trace.StateChanges.Count, "state changes count");
            Assert.True(trace.StateChanges.ContainsKey(Sender), "sender");
            Assert.True(trace.StateChanges.ContainsKey(Recipient), "recipient");
            Assert.True(trace.StateChanges.ContainsKey(Miner), "miner");
            Assert.AreEqual(UInt256.Zero, trace.StateChanges[Sender].Nonce.Before, "sender before");
            Assert.AreEqual(UInt256.One, trace.StateChanges[Sender].Nonce.After, "sender after");
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
            (Block block, Transaction transaction) = PrepareInitTx(BlockNumber, 100000, code);
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
