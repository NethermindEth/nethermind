/*
 * Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1014Tests : VirtualMachineTestsBase
    {
        protected override UInt256 BlockNumber => MainNetSpecProvider.ConstantinopleBlockNumber;

        private void AssertEip1014(Address address, byte[] code)
        {
            AssertCodeHash(address, Keccak.Compute(code));
        }

        [Test]
        public void Test()
        {
            byte[] salt = {4, 5, 6};   
            
            byte[] deployedCode = {1, 2, 3};
            
            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode).Done;
            
            byte[] createCode = Prepare.EvmCode
                .Create2(initCode, salt, 0).Done;

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestObject.AddressC, 50000)
                .PushData(Address.OfContract(TestObject.AddressC, 0))
                .Op(Instruction.EXTCODEHASH)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            
            Address expectedAddress = Address.OfContract(TestObject.AddressC, salt.PadLeft(32).AsSpan(), initCode.AsSpan());
            AssertEip1014(expectedAddress, deployedCode);
        }
    }
}