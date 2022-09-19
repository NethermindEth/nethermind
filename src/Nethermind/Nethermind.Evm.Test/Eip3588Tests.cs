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

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;
using FluentAssertions.Execution;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip3855Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;

        private void AssertEip3855(Address address, byte[] code)
        {
            AssertCodeHash(address, Keccak.Compute(code));
        }

        private TestAllTracerWithOutput testBase(int repeat)
        {
            Prepare codeInitializer = Prepare.EvmCode;
            for (int i = 0; i < repeat; i++)
            {
                codeInitializer.Op(Instruction.PUSH0);
            }

            byte[] code = codeInitializer.Done;
            TestAllTracerWithOutput receipt = Execute(code);
            return receipt;
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(123)]
        [TestCase(1024)]
        public void Test_Eip3855(int repeat)
        {
            TestAllTracerWithOutput receipt = testBase(repeat);
            receipt.StatusCode.Should().Be(StatusCode.Success);
            receipt.GasSpent.Should().Be(repeat * GasCostOf.Base + GasCostOf.Transaction);
        }

        [TestCase(1025)]
        [TestCase(1026)]
        public void Test_StackvOverFlow(int repeat)
        {
            TestAllTracerWithOutput receipt = testBase(repeat);

            receipt.StatusCode.Should().Be(StatusCode.Failure);
            receipt.Error.Should().Be(EvmExceptionType.StackOverflow.ToString());

        }

    }
}
