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
using System.Collections.Immutable;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture(VirtualMachineTestsStateProvider.MerkleTrie)]
    [TestFixture(VirtualMachineTestsStateProvider.VerkleTrie)]
    public class AccessTxTracerTests : VirtualMachineTestsBase
    {
        [Test]
        public void Records_get_correct_accessed_addresses()
        {
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;
            
            TestState.Commit(Berlin.Instance);
            
            (AccessTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            IEnumerable<Address> addressesAccessed = tracer.AccessList.Data.Keys;
            IEnumerable<Address> expected = new[] {
                SenderRecipientAndMiner.Default.Sender, SenderRecipientAndMiner.Default.Recipient, TestItem.AddressC
            };

            Assert.IsNotEmpty(addressesAccessed);
            addressesAccessed.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void Records_get_correct_accessed_keys()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x69")
                .Op(Instruction.SSTORE)
                .Done;
            
            (AccessTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> accessedData = tracer.AccessList.Data;

            Assert.IsNotEmpty(accessedData);
            accessedData.Should().BeEquivalentTo(
                new Dictionary<Address, IReadOnlySet<UInt256>>{
                    {SenderRecipientAndMiner.Default.Sender, ImmutableHashSet<UInt256>.Empty}, 
                    {SenderRecipientAndMiner.Default.Recipient, new HashSet<UInt256>{105}}});
        }
        
        protected override ISpecProvider SpecProvider => new TestSpecProvider(Berlin.Instance);
        
        protected (AccessTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceAccessCall(SenderRecipientAndMiner addresses, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
            AccessTxTracer tracer = new();
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer, block, transaction);
        }

        public AccessTxTracerTests(VirtualMachineTestsStateProvider stateProvider) : base(stateProvider)
        {
        }
    }
}
