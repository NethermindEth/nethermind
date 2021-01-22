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
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallDataCopyTests : VirtualMachineTestsBase
    {
        [Test]
        public void Ranges()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData("0x1e4e2")
                .PushData("0x5050600163306e2b386347355944f3636f376163636d6b")
                .Op(Instruction.CALLDATACOPY)
                .Done;

            var result = Execute(code);
            result.Error.Should().BeNull();
        }
    }
}
