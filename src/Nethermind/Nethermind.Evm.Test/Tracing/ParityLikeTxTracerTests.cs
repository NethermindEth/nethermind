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
using Nethermind.Evm.Precompiles;
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
        public void On_failure_gas_used_is_gas_limit()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(100000, trace.Action.Result.GasUsed);
        }

        [Test]
        public void On_failure_output_is_empty()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            (ParityLikeTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceParityCall(code);
            Assert.AreEqual(Bytes.Empty, trace.Action.Result.Output);
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
            Assert.AreEqual("init", trace.Action.CallType);
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
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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

            Assert.AreEqual("staticCall", trace.Action.Subtraces[0].CallType, "[0] type");
        }

        [Test]
        public void Can_trace_precompile_calls()
        {
            byte[] code = Prepare.EvmCode
                .Call(IdentityPrecompiledContract.Instance.Address, 50000)
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
            Assert.AreEqual(IdentityPrecompiledContract.Instance.Address, trace.Action.Subtraces[0].To, "[0] to");
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
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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
            Assert.AreEqual(new byte[] {0}, trace.StateChanges[Recipient].Storage[2].Before, "recipient storage[2]");
            Assert.AreEqual(Bytes.FromHexString(SampleHexData1), trace.StateChanges[Recipient].Storage[2].After, "recipient storage[2] after");
            Assert.AreEqual(new byte[] {0}, trace.StateChanges[Recipient].Storage[3].Before, "recipient storage[3]");
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
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

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
            Assert.AreEqual(Bytes.Empty, trace.StateChanges[Contract].Code.Before, "code before");
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
            Assert.True(trace.StateChanges.ContainsKey(Miner), "sender");
            Assert.AreEqual(100.Ether(), trace.StateChanges[Sender].Balance.Before, "sender before");
            Assert.AreEqual(100.Ether() - 21001, trace.StateChanges[Sender].Balance.After, "sender after");
            Assert.AreEqual(100.Ether(), trace.StateChanges[Recipient].Balance.Before, "recipient before");
            Assert.AreEqual(100.Ether() + 1, trace.StateChanges[Recipient].Balance.After, "recipient after");
            Assert.AreEqual(0.Ether(), trace.StateChanges[Miner].Balance.Before, "miner before");
            Assert.AreEqual(0.Ether() + 21000, trace.StateChanges[Miner].Balance.After, "miner after");
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
    }
}