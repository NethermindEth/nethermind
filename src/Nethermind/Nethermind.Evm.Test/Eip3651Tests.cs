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
using Nethermind.Core;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class Eip3651Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ShanghaiBlockNumber;

        [Test]
        public void Access_beneficiary_address_after_eip_3651()
        {
            byte[] code = Prepare.EvmCode
                .PushData(MinerKey.Address)
                .Op(Instruction.BALANCE)
                .Op(Instruction.POP)
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 105);
        }

        [Test]
        public void Access_beneficiary_address_before_eip_3651()
        {
            TestState.CreateAccount(TestItem.AddressF, 100.Ether());
            byte[] code = Prepare.EvmCode
                .PushData(MinerKey.Address)
                .Op(Instruction.BALANCE)
                .Op(Instruction.POP)
                .Done;
            TestAllTracerWithOutput result = Execute(BlockNumber - 1, 100000, code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 2605);
        }

        protected override TestAllTracerWithOutput CreateTracer()
        {
            TestAllTracerWithOutput tracer = base.CreateTracer();
            tracer.IsTracingAccess = false;
            return tracer;
        }

    }
}
