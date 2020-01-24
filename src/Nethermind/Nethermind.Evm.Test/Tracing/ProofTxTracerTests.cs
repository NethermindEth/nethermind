//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Evm.Tracing.Proofs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class ProofTxTracerTests : VirtualMachineTestsBase
    {
        [Test]
        public void Can_trace_sender_recipient_miner()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(3, trace.Accounts.Count, "count");
            Assert.Contains(Sender, trace.Accounts);
            Assert.Contains(Recipient, trace.Accounts);
            Assert.Contains(Miner, trace.Accounts);
        }
        
        [Test]
        public void Can_trace_sender_recipient_miner_when_all_are_same()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .Done;

            SenderRecipientAndMiner addresses = new SenderRecipientAndMiner();
            addresses.RecipientKey = SenderKey;
            addresses.MinerKey = SenderKey;
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(addresses, code);
            Assert.AreEqual(1, trace.Accounts.Count, "count");
            Assert.Contains(Sender, trace.Accounts);
        }
        
        [Test]
        public void Can_trace_touch_only_null_accounts()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .PushData(TestItem.AddressC.Bytes)
                .Op(Instruction.BALANCE)
                .Done;
            
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(4, trace.Accounts.Count, "count");
            Assert.Contains(TestItem.AddressC, trace.Accounts);
        }
        
        [Test]
        public void Can_trace_touch_only_preexisting_accounts()
        {
            TestState.CreateAccount(TestItem.AddressC, 100);
            TestState.Commit(Spec);
            
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .PushData(TestItem.AddressC.Bytes)
                .Op(Instruction.BALANCE)
                .Done;
            
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(4, trace.Accounts.Count, "count");
            Assert.Contains(TestItem.AddressC, trace.Accounts);
        }
        
        [Test]
        public void Can_trace_touch_only_null_miner_accounts()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .PushData(SenderRecipientAndMiner.Default.Miner.Bytes)
                .Op(Instruction.BALANCE)
                .Done;
            
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(3, trace.Accounts.Count, "count");
        }
        
        [Test]
        public void Can_trace_blockhash()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(1, trace.BlockHashes.Count, "count");
        }
        
        [Test]
        public void Can_trace_result()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x03")
                .PushData("0x00")
                .Op(Instruction.RETURN)
                .Done;
            
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(3, trace.Output.Length);
        }
        
        [Test]
        public void Can_trace_on_failure()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x03")
                .PushData("0x00")
                .PushData("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")
                .PushData("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")
                .Op(Instruction.MSTORE)
                .Op(Instruction.RETURN)
                .Done;
            
            (ProofTxTrace trace, Block block, Transaction tx) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
            Assert.AreEqual(3, trace.Accounts.Count);
            Assert.AreEqual(0, trace.Output.Length);
        }

        protected (ProofTxTrace trace, Block block, Transaction tx) ExecuteAndTraceProofCall(SenderRecipientAndMiner addresses, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
            ProofTxTracer tracer = new ProofTxTracer();
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer.Build(), block, transaction);
        }
    }
}