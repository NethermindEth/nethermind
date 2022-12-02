// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
