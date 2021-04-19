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
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip3198BaseFeeTests : VirtualMachineTestsBase
    {
        const long LondonTestBlockNumber = 5;
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

        [TestCase(true, 0)]
        [TestCase(true, 100)]
        [TestCase(true, 20)]
        [TestCase(false, 20)]
        [TestCase(false, 0)]
        public void Basefee_opcode_should_return_expected_results(bool eip3198Enabled, int baseFee)
        {
           _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            byte[] code = Prepare.EvmCode
                .Op(Instruction.BASEFEE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            long blockNumber = eip3198Enabled ? LondonTestBlockNumber : LondonTestBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx(blockNumber, 100000, code);
            block.Header.BaseFee = (UInt256)baseFee;
            transaction.FeeCap = (UInt256)baseFee;
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            if (eip3198Enabled)
            {
                AssertStorage((UInt256)0, (UInt256)baseFee);
            }
            else
            {
                tracer.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
                AssertStorage((UInt256)0, (UInt256)0);
            }
        }
    }
}
