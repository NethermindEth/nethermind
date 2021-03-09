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
    [TestFixture]
    public class CodeInfoTests
    {
        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void Validates_when_only_jump_dest_present(int destination, bool isValid)
        {
            byte[] code =
            {
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);
            
            codeInfo.ValidateJump(destination, false).Should().Be(isValid);
        }
        
        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void Validates_when_only_begin_sub_present(int destination, bool isValid)
        {
            byte[] code =
            {
                (byte)Instruction.BEGINSUB
            };

            CodeInfo codeInfo = new(code);


            codeInfo.ValidateJump(destination, true).Should().Be(isValid);
        }

        [Test]
        public void Validates_when_push_with_data_like_jump_dest()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH1,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);

            codeInfo.ValidateJump(1, true).Should().BeFalse();
            codeInfo.ValidateJump(1, false).Should().BeFalse();
        }
        
        [Test]
        public void Validates_when_push_with_data_like_begin_sub()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH1,
                (byte)Instruction.BEGINSUB
            };

            CodeInfo codeInfo = new(code);

            codeInfo.ValidateJump(1, true).Should().BeFalse();
            codeInfo.ValidateJump(1, false).Should().BeFalse();
        }
    }
}
