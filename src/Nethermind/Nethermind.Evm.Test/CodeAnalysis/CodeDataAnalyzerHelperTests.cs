// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    [TestFixture]
    public class CodeDataAnalyzerHelperTests
    {
        [Test]
        public void Validate_CodeBitmap_With_Push10()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH10,
                1,2,3,4,5,6,7,8,9,10,
                (byte)Instruction.JUMPDEST
            };

            var bitmap = CodeDataAnalyzerHelper.CreateCodeBitmap(new ByteCode(code));
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

            var bitmap = CodeDataAnalyzerHelper.CreateCodeBitmap(new ByteCode(code));
            bitmap[0].Should().Be(127);
            bitmap[1].Should().Be(255);
            bitmap[2].Should().Be(255);
            bitmap[3].Should().Be(254);
        }
    }
}
