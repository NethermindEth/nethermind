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

using Nethermind.Core;
using Nethermind.Core.Extensions;
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
            Assert.AreEqual("[]", trace.TraceAddress);
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
            Assert.AreEqual(100000, trace.Action.Gas);
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
            Assert.AreEqual(21003, trace.Result.GasUsed);
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
            Assert.AreEqual(Bytes.FromHexString(SampleHexData1.PadLeft(64, '0')), trace.Result.Output);
        }
    }
}