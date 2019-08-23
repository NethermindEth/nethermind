/*
 * Copyright (c) 2018 Demerzel Solutions LimitedZ
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip1884Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainNetSpecProvider.IstanbulBlockNumber;
        protected override ISpecProvider SpecProvider => new CustomSpecProvider(0, (0, Istanbul.Instance));
        
        [Test]
        public void after_istanbul_selfbalance_opcode_puts_current_address_balance_onto_the_stack()
        {
            var code = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.SelfBalance + GasCostOf.SSet);
            var balance = TestState.GetBalance(Recipient);
            AssertStorage(0, balance);
        }
    }
}