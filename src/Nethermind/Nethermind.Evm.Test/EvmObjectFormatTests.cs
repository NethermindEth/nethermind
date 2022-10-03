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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Specs.Forks;
using NSubstitute;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Blockchain;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EvmObjectFormatTests : VirtualMachineTestsBase
    {
        // valid code
        [TestCase("0xEF00010100010000", true, 1, 0, true)]
        [TestCase("0xEF0001010002006000", true, 2, 0, true)]
        [TestCase("0xEF0001010002020001006000AA", true, 2, 1, true)]
        [TestCase("0xEF0001010002020004006000AABBCCDD", true, 2, 4, true)]
        [TestCase("0xEF00010100040200020060006001AABB", true, 4, 2, true)]
        [TestCase("0xEF000101000602000400600060016002AABBCCDD", true, 6, 4, true)]
        // code with invalid magic will NOT fail following EIP-3541 or EIP-3540 
        [TestCase("", true, 0, 0, true, Description = "Empty code")]
        [TestCase("0xFE", true, 0, 0, true, Description = "Codes starting with invalid magic first byte")]
        [TestCase("0xFE0001010002020004006000AABBCCDD", true, 0, 0, true, Description = "Valid code with wrong magic first byte")]
        // code with invalid magic will fail following EIP-3541
        [TestCase("0xEF", false, 0, 0, true, Description = "Incomplete Magic")]
        [TestCase("0xEF01", false, 0, 0, true, Description = "Incorrect Magic second byte")]
        [TestCase("0xEF0101010002020004006000AABBCCDD", false, 0, 0, true, Description = "Valid code with wrong magic second byte")]
        // code with valid magic but invalid body
        [TestCase("0xEF0000010002020004006000AABBCCDD", false, 0, 0, true, Description = "Invalid Version")]
        [TestCase("0xEF00010100", false, 0, 0, true, Description = "Code section missing")]
        [TestCase("0xEF0001010002006000DEADBEEF", false, 0, 0, true, Description = "Invalid total Size")]
        [TestCase("0xEF00010100020100020060006000", false, 0, 0, true, Description = "Multiple Code sections")]
        [TestCase("0xEF000101000002000200AABB", false, 0, 0, true, Description = "Empty code section")]
        [TestCase("0xEF000102000401000200AABBCCDD6000", false, 0, 0, true, Description = "Data section before code section")]
        [TestCase("0xEF000101000202", false, 0, 0, true, Description = "Data Section size Missing")]
        [TestCase("0xEF0001010002020004020004006000AABBCCDDAABBCCDD", false, 0, 0, true, Description = "Multiple Data sections")]
        [TestCase("0xEF0001010002030004006000AABBCCDD", false, 0, 0, true, Description = "Unknown Section")]
        public void EOF_Compliant_formats_Test(string code, bool isCorrectFormated, int codeSize, int dataSize, bool isShanghaiFork) 
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            IReleaseSpec spec = isShanghaiFork ? Shanghai.Instance : GrayGlacier.Instance;

            var expectedHeader = codeSize == 0 && dataSize == 0
                ? null
                : new EofHeader{
                    CodeSize = (ushort)codeSize,
                    DataSize = (ushort)dataSize
                };
            var checkResult = bytecode.ValidateByteCode(spec, out var header);

            if(isShanghaiFork)
            {
                header.Should().Be(expectedHeader);
                checkResult.Should().Be(isCorrectFormated);
            } else
            {
                checkResult.Should().Be(isCorrectFormated);
            }
        }
    }
}
