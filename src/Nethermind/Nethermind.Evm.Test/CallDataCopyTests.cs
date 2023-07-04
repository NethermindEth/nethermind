// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallDataCopyTests : VirtualMachineTestsBase
    {
        [Test]
        public void Ranges()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData("0x1e4e2")
                .PushData("0x5050600163306e2b386347355944f3636f376163636d6b")
                .Op(Instruction.CALLDATACOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.Error.Should().BeNull();
        }
    }
}
