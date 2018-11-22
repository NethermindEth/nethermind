/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    /*
     *{
     *    "action": {
     *      "callType": "call",
     *      "from": "0x430adc807210dab17ce7538aecd4040979a45137",
     *      "gas": "0x1a1f8",
     *      "input": "0x",
     *      "to": "0x9bcb0733c56b1d8f0c7c4310949e00485cae4e9d",
     *      "value": "0x2707377c7552d8000"
     *    },
     *    "blockHash": "0x3aa472d57e220458fe5b9f1587b9211de68b27504064f5f6e427c68fc1691a29",
     *    "blockNumber": 2392500,
     *    "result": {
     *      "gasUsed": "0x2162",
     *      "output": "0x"
     *    },
     *    "subtraces": 2,
     *    "traceAddress": [],
     *    "transactionHash": "0x847ed5e2e9430bc6ee925a81137ebebe0cea1352209f96723d3503eb7a707aa8",
     *    "transactionPosition": 42,
     *    "type": "call"
     *  },
     *  {
     *    "action": {
     *      "callType": "call",
     *      "from": "0x9bcb0733c56b1d8f0c7c4310949e00485cae4e9d",
     *      "gas": "0x13f57",
     *      "input": "0x16c72721",
     *      "to": "0x2bd2326c993dfaef84f696526064ff22eba5b362",
     *      "value": "0x0"
     *    },
     *    "blockHash": "0x3aa472d57e220458fe5b9f1587b9211de68b27504064f5f6e427c68fc1691a29",
     *    "blockNumber": 2392500,
     *    "result": {
     *      "gasUsed": "0xc5",
     *      "output": "0x0000000000000000000000000000000000000000000000000000000000000001"
     *    },
     *    "subtraces": 0,
     *    "traceAddress": [
     *      0
     *    ],
     *    "transactionHash": "0x847ed5e2e9430bc6ee925a81137ebebe0cea1352209f96723d3503eb7a707aa8",
     *    "transactionPosition": 42,
     *    "type": "call"
     *  },
     *  {
     *    "action": {
     *      "callType": "call",
     *      "from": "0x9bcb0733c56b1d8f0c7c4310949e00485cae4e9d",
     *      "gas": "0x8fc",
     *      "input": "0x",
     *      "to": "0x9e6316f44baeeee5d41a1070516cc5fa47baf227",
     *      "value": "0x2707377c7552d8000"
     *    },
     *    "blockHash": "0x3aa472d57e220458fe5b9f1587b9211de68b27504064f5f6e427c68fc1691a29",
     *    "blockNumber": 2392500,
     *    "result": {
     *      "gasUsed": "0x0",
     *      "output": "0x"
     *    },
     *    "subtraces": 0,
     *    "traceAddress": [
     *      1
     *    ],
     *    "transactionHash": "0x847ed5e2e9430bc6ee925a81137ebebe0cea1352209f96723d3503eb7a707aa8",
     *    "transactionPosition": 42,
     *    "type": "call"
     *  }
     *
     */

    [TestFixture]
    public class ParityLikeCallTxTracerTests : VirtualMachineTestsBase
    {
        private const string SampleHexData1 = "a01234";
        private const string SampleHexData2 = "b15678";
        private const string HexZero = "00";

        [Test]
        public void On_failure_gas_used_is_gas_limit()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(100000, trace.Action.Result.GasUsed);
        }

        [Test]
        public void On_failure_output_is_empty()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Bytes.Empty, trace.Action.Result.Output);
        }

        [Test]
        public void On_failure_block_and_tx_fields_are_set()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(tx.SenderAddress, trace.Action.From, "from");
            Assert.AreEqual(tx.To, trace.Action.To, "to");
            Assert.AreEqual(block.Hash, trace.BlockHash, "hash");
            Assert.AreEqual(block.Number, trace.BlockNumber, "number");
            Assert.AreEqual(0, trace.TransactionPosition, "tx index");
            Assert.AreEqual(tx.Hash, trace.TransactionHash, "tx hash");
            Assert.AreEqual("call", trace.Type, "type");
            Assert.AreEqual((long) tx.GasLimit - 21000, trace.Action.Gas, "gas");
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

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(block.Hash, trace.BlockHash);
        }

        [Test]
        [Todo(Improve.TestCoverage, "Add scenario where tx index is not 0")]
        public void Tx_index_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(0, trace.TransactionPosition);
        }

        [Test]
        public void Trace_address_is_valid_in_simple_cases()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Array.Empty<int>(), trace.Action.TraceAddress);
        }

        [Test]
        public void Tx_hash_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(tx.Hash, trace.TransactionHash);
        }

        [Test]
        public void Type_is_set_when_call()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual("call", trace.Type);
        }

        [Test]
        public void Type_is_set_when_init()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteInitAndTraceParityCall(initCode);
            Assert.AreEqual("init", trace.Type);
        }

        [Test]
        public void Action_gas_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(79000, trace.Action.Gas);
        }

        [Test]
        public void Action_call_type_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual("call", trace.Action.CallType);
        }

        [Test]
        public void Action_from_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Sender, trace.Action.From);
        }

        [Test]
        public void Action_to_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
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

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(input, value, code);
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

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(input, value, code);
            Assert.AreEqual(value, trace.Action.Value);
        }

        [Test]
        public void Action_result_gas_is_set()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(21003, trace.Action.Result.GasUsed);
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

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
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

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestObject.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
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
            Assert.AreEqual(new[] {0}, trace.Action.Subtraces[0].TraceAddress, "[0] address");
            Assert.AreEqual(0, trace.Action.Subtraces[0].Subtraces[0].Subtraces.Count, "[0, 0] subtraces");
            Assert.AreEqual(new[] {0, 0}, trace.Action.Subtraces[0].Subtraces[0].TraceAddress, "[0, 0] address");
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

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .DelegateCall(TestObject.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, // DELEGATE CALL
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP 
                1, // STOP
            };

            Assert.AreEqual("delegateCall", trace.Action.Subtraces[0].CallType, "[0] type");
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

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .CallCode(TestObject.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL CODE
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP 
                1, // STOP
            };

            Assert.AreEqual("callCode", trace.Action.Subtraces[0].CallType, "[0] type");
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

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .StaticCall(TestObject.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL CODE
                3, 3, 3, 3, 3, 3, // CREATE
                2, // STOP 
                1, // STOP
            };

            Assert.AreEqual("staticCall", trace.Action.Subtraces[0].CallType, "[0] type");
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

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestObject.AddressC, 40000)
                .Call(TestObject.AddressC, 40000)
                .Op(Instruction.STOP)
                .Done;

            (ParityLikeCallTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
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
            Assert.AreEqual(new[] {0}, trace.Action.Subtraces[0].TraceAddress, "[0] address");
            Assert.AreEqual("call", trace.Action.Subtraces[0].CallType, "[0] type");
            
            Assert.AreEqual(1, trace.Action.Subtraces[1].Subtraces.Count, "[1] subtraces");
            Assert.AreEqual(new[] {1}, trace.Action.Subtraces[1].TraceAddress, "[1] address");
            Assert.AreEqual("call", trace.Action.Subtraces[1].CallType, "[1] type");

            Assert.AreEqual(new[] {0, 0}, trace.Action.Subtraces[0].Subtraces[0].TraceAddress, "[0, 0] address");
            Assert.AreEqual(0, trace.Action.Subtraces[0].Subtraces[0].Subtraces.Count, "[0, 0] subtraces");
            Assert.AreEqual("init", trace.Action.Subtraces[1].Subtraces[0].CallType, "[0, 0] type");
            
            Assert.AreEqual(new[] {1, 0}, trace.Action.Subtraces[1].Subtraces[0].TraceAddress, "[1, 0] address");
            Assert.AreEqual(0, trace.Action.Subtraces[1].Subtraces[0].Subtraces.Count, "[1, 0] subtraces");
            Assert.AreEqual("init", trace.Action.Subtraces[1].Subtraces[0].CallType, "[1, 0] type");
        }
    }
}