// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class SignExtTests : VirtualMachineTestsBase
    {
        [TestCase(0, 0, Description = "Sign extend zero")]
        [TestCase(255, -1, Description = "Sign extend max")]
        public void Sign_ext_value(int value, int expectedResult)
        {
            UInt256 expected = expectedResult == -1 ? UInt256.MaxValue : (UInt256)expectedResult;
            byte[] code = Prepare.EvmCode
                .PushData(value)
                .PushData(0)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, expected);
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
