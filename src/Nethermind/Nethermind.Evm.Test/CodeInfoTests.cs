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
        
        [Test]
        public void Validate_CodeBitmap_With_Push10()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH10,
                1,2,3,4,5,6,7,8,9,10,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);
            
            codeInfo.ValidateJump(11, false).Should().BeTrue();

            var bitmap = CodeInfoHelper.CreateCodeBitmap(code);
            bitmap[0].Should().Be(127);
            bitmap[1].Should().Be(224);
        }
        
        [Test]
        public void Validate_CodeBitmap_With_Push30()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH30,
                1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);
            
            codeInfo.ValidateJump(31, false).Should().BeTrue();

            var bitmap = CodeInfoHelper.CreateCodeBitmap(code);
            bitmap[0].Should().Be(127);
            bitmap[1].Should().Be(255);
            bitmap[2].Should().Be(255);
            bitmap[3].Should().Be(254);
        }
    }
}
