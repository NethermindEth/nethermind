// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class SModTests : VirtualMachineTestsBase
    {
        [TestCase(1, 1, 0)]
        [TestCase(1, 0, 0)]
        [TestCase(1, -1, 0)]
        [TestCase(0, 1, 0)]
        [TestCase(0, 0, 0)]
        [TestCase(0, -1, 0)]
        [TestCase(-1, 1, 0)]
        [TestCase(-1, 0, 0)]
        [TestCase(-1, -1, 0)]
        [TestCase(1, 2, 1)]
        [TestCase(1, -2, 1)]
        [TestCase(-1, 2, -1)]
        [TestCase(-1, -2, -1)]
        public void Sgt(int a, int b, int res)
        {
            byte[] code = Prepare.EvmCode
                .PushData((UInt256)new Int256.Int256(b))
                .PushData((UInt256)new Int256.Int256(a))
                .Op(Instruction.SMOD)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, res);
        }

        [TestCase(-3, -2)]
        [TestCase(3, -2)]
        public void Test_for_a_equals_int256_dot_min(int b, int res)
        {
            byte[] code = Prepare.EvmCode
                .PushData((UInt256)new Int256.Int256(b))
                .PushData(new UInt256(0ul, 0ul, 0ul, 0x8000000000000000ul))
                .Op(Instruction.SMOD)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, res);

        }
    }
}
