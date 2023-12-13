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

        [Test]
        public void Simple_routine()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x60045e005c5d")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            // result.StatusCode.Should().Be(1);
            // AssertGas(result, GasCostOf.Transaction + 18);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }

        [Test]
        public void Two_levels_of_subroutines()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x6800000000000000000c5e005c60115e5d5c5d")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            // result.StatusCode.Should().Be(1);
            // AssertGas(result, GasCostOf.Transaction + 36);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }

        [Test]
        public void Invalid_jump()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x6801000000000000000c5e005c60115e5d5c5d")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(0);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }

        [Test]
        public void Shallow_return_stack()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x5d5858")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(0);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }

        [Test]
        public void Subroutine_at_end_of_code()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x6005565c5d5b60035e")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            // result.StatusCode.Should().Be(1);
            // AssertGas(result, GasCostOf.Transaction + 30);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }

        [Test]
        public void Error_on_walk_into_the_subroutine()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x5c5d00")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(0);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }
    }
}
