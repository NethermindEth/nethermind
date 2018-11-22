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

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
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