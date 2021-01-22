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

using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class SDivTests : VirtualMachineTestsBase
    {
        [Test]
        public void Sign_ext_zero()
        {
            byte[] a = BigInteger.Parse("-57896044618658097711785492504343953926634992332820282019728792003956564819968").ToBigEndianByteArray(32);
            byte[] b = BigInteger.Parse("-1").ToBigEndianByteArray(32);

            byte[] code = Prepare.EvmCode
                .PushData(b)
                .PushData(a)
                .Op(Instruction.SDIV)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, -BigInteger.Pow(2, 255));
            AssertStorage(UInt256.Zero, BigInteger.Pow(2, 255));
        }

        [Test]
        public void Representations()
        {
            byte[] a = BigInteger.Parse("-57896044618658097711785492504343953926634992332820282019728792003956564819968").ToBigEndianByteArray(32);
            byte[] b = BigInteger.Parse("57896044618658097711785492504343953926634992332820282019728792003956564819968").ToBigEndianByteArray(32);

            Assert.AreEqual(a, b);
        }
    }
}
