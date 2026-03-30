// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip2315Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.BerlinBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [TestCase("0x60045e005c5d", Description = "Simple routine")]
        [TestCase("0x6800000000000000000c5e005c60115e5d5c5d", Description = "Two levels of subroutines")]
        [TestCase("0x6801000000000000000c5e005c60115e5d5c5d", Description = "Invalid jump")]
        [TestCase("0x5d5858", Description = "Shallow return stack")]
        [TestCase("0x6005565c5d5b60035e", Description = "Subroutine at end of code")]
        [TestCase("0x5c5d00", Description = "Error on walk into the subroutine")]
        public void All_subroutine_opcodes_are_bad_instructions(string codeHex)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether);

            byte[] code = Prepare.EvmCode
                .FromCode(codeHex)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }
    }
}
