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

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    // Alex Beregszaszi, Paweł Bylica, Andrei Maiboroda, Alexey Akhunov, Christian Reitwiessner, Martin Swende, "EIP-3541: Reject new contracts starting with the 0xEF byte," Ethereum Improvement Proposals, no. 3541, March 2021. [Online serial]. Available: https://eips.ethereum.org/EIPS/eip-3541.
    public class Eip3541Tests : VirtualMachineTestsBase
    {
        const long LondonTestBlockNumber = 4_370_000;
        protected override ISpecProvider SpecProvider
        {
            get
            {
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpec(Arg.Is<long>(x => x >= LondonTestBlockNumber)).Returns(London.Instance);
                specProvider.GetSpec(Arg.Is<long>(x => x < LondonTestBlockNumber)).Returns(Berlin.Instance);
                return specProvider;
            }
        }
        
        [TestCase(false)]
        [TestCase(true)]
        public void Wrong_contract_creation_should_return_bad_instruction(bool eip3541Enabled)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .Op(Instruction.INVALIDCONTRACTCREATION)
                .Done;
            
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            long blockNumber = eip3541Enabled ? LondonTestBlockNumber : LondonTestBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx(blockNumber, 100000, code);

            transaction.GasPrice = 20.GWei();
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);
            
            tracer.Error.Should().Be("BadInstruction");
            tracer.StatusCode.Should().Be(0);
        }
    }
}
