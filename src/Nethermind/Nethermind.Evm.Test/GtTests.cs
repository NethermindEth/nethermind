// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
