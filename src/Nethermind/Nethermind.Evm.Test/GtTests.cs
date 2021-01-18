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

using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class GtTests : VirtualMachineTestsBase
    {
        [TestCase(0, 0, 0)]
        [TestCase(int.MaxValue, int.MaxValue, 0)]
        [TestCase(1, 0, 1)]
        [TestCase(2, 1, 1)]
        [TestCase(0, 1, 0)]
        [TestCase(2, 2, 0)]
        public void Gt(int a, int b, int res)
        {
            byte[] code = Prepare.EvmCode
                .PushData(new UInt256((ulong)b))
                .PushData(new UInt256((ulong)a))
                .Op(Instruction.GT)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, res);
        }
    }
}
