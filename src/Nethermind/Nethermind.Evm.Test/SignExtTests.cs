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

using FluentAssertions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class SignExtTests : VirtualMachineTestsBase
    {
        [Test]
        public void Sign_ext_zero()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }

        [Test]
        public void Sign_ext_max()
        {
            byte[] code = Prepare.EvmCode
                .PushData(255)
                .PushData(0)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, UInt256.MaxValue);
        }

        [Test]
        public void Sign_ext_underflow()
        {
            byte[] code = Prepare.EvmCode
                .PushData(32)
                .Op(Instruction.SIGNEXTEND)
                .Done;

            TestAllTracerWithOutput res = Execute(code);
            res.Error.Should().Be(EvmExceptionType.StackUnderflow.ToString());
        }
    }
}
