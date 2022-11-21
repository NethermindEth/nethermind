// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class SltTests : VirtualMachineTestsBase
    {
        [TestCase(1, 1, 0)]
        [TestCase(1, 0, 0)]
        [TestCase(1, -1, 0)]
        [TestCase(0, 1, 1)]
        [TestCase(0, 0, 0)]
        [TestCase(0, -1, 0)]
        [TestCase(-1, 1, 1)]
        [TestCase(-1, 0, 1)]
        [TestCase(-1, -1, 0)]
        [TestCase(-1, -2, 0)]
        [TestCase(-2, -1, 1)]
        public void Slt(int a, int b, int res)
        {
            byte[] code = Prepare.EvmCode
                .PushData((UInt256)new Int256.Int256(b))
                .PushData((UInt256)new Int256.Int256(a))
                .Op(Instruction.SLT)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, res);
        }
    }
}
