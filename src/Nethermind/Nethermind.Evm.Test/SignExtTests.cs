// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
