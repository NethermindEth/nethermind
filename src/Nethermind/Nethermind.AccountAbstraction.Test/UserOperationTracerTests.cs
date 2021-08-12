//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationTracerTests : VirtualMachineTestsBase
    {
        [Test]
        public void Can_trace_accessed_storage_correctly()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x69")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .PushData("0x10")
                .Op(Instruction.SLOAD)
                .Done;
            
            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            var accessedData = tracer.AccessedStorage;
            var expectedDictionary = new Dictionary<Address, HashSet<UInt256>>
            {
                {SenderRecipientAndMiner.Default.Recipient, new HashSet<UInt256>{105, 16}}
            };
            accessedData.Should().BeEquivalentTo(expectedDictionary);

            tracer.Success.Should().BeTrue();
        }
        
        [Test]
        public void Should_fail_if_SSTORE_is_used()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x69")
                .Op(Instruction.SSTORE)
                .Done;
            
            (UserOperationTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            tracer.Success.Should().BeFalse();
        }
        
        private (UserOperationTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceAccessCall(SenderRecipientAndMiner addresses, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
            UserOperationTxTracer tracer = new(SenderRecipientAndMiner.Default.Miner, transaction);
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer, block, transaction);
        }
    }
}
