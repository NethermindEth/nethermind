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

            var result = Execute(code);
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

            var result = Execute(code);
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

            var result = Execute(code);
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

            var result = Execute(code);
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

            var result = Execute(code);
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

            var result = Execute(code);
            result.StatusCode.Should().Be(0);
            result.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }
    }
}
