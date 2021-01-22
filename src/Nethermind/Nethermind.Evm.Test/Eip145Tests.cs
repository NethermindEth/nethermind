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

using System;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip145Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => RopstenSpecProvider.ConstantinopleBlockNumber;
        
        protected override ISpecProvider SpecProvider => RopstenSpecProvider.Instance;

        private void AssertEip145(TestAllTracerWithOutput receipt, byte result)
        {
            AssertEip145(receipt, new[] {result});
        }

        private void AssertEip145(TestAllTracerWithOutput receipt, string result)
        {
            AssertEip145(receipt, Bytes.FromHexString(result));
        }
        
        private void AssertEip145(TestAllTracerWithOutput receipt, ReadOnlySpan<byte> result)
        {
            AssertStorage(0, result);
            AssertGas(receipt, result.IsZero() ? ZeroResultGas : NonZeroResultGas);
        }

        private const long ZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SStoreNetMeteredEip1283;
        private const long NonZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet;

        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x00", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000002")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0xff", "0x8000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x0101", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x00", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0x8000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe")]
        public void Shift_left(string a, string b, string result)
        {
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.SHL)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            var receipt = Execute(code);
            AssertEip145(receipt, result);
        }
        
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x00", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x4000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0xff", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x0101", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x00", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        public void Shift_right(string a, string b, string result)
        {
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.SHR)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertEip145(receipt, result);
        }
        
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x00", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x01", "0xc000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0xff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x0100", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0x8000000000000000000000000000000000000000000000000000000000000000", "0x0101", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x00", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x100", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x4000000000000000000000000000000000000000000000000000000000000000", "0xfe", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xf8", "0x000000000000000000000000000000000000000000000000000000000000007f")]
        [TestCase("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xfe", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        public void Arithmetic_shift_right(string a, string b, string result)
        {
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.SAR)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertEip145(receipt, result);
        }
    }
}
