// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class ExtCodeCopyTests : VirtualMachineTestsBase
    {
        [Test]
        public void Ranges()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .Op(Instruction.EXTCODECOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.Error.Should().BeNull();
        }
    }
}
