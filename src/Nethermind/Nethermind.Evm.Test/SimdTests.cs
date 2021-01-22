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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.Self)]
    public class SimdTests : VirtualMachineTestsBase
    {
        private readonly bool _simdDisabled;
        protected override long BlockNumber => MainnetSpecProvider.ConstantinopleFixBlockNumber;

        private void AssertEip1014(Address address, byte[] code)
        {
            AssertCodeHash(address, Keccak.Compute(code));
        }

        public SimdTests(bool simdDisabled)
        {
            _simdDisabled = simdDisabled;
        }
        
        [Test]
        public void And()
        {
            if (_simdDisabled)
            {
                Machine.DisableSimdInstructions();
            }
            
            byte[] a = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0ff");
            byte[] b = Bytes.FromHexString("0x0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            byte[] result = Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000000f");
            
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.AND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            var receipt = Execute(code);
            AssertSimd(receipt, result);
        }
        
        [Test]
        public void Or()
        {
            if (_simdDisabled)
            {
                Machine.DisableSimdInstructions();
            }
            
            byte[] a = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0");
            byte[] b = Bytes.FromHexString("0x0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            byte[] result = Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
            
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.OR)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            var receipt = Execute(code);
            AssertSimd(receipt, result);
        }
        
        [Test]
        public void Xor()
        {
            if (_simdDisabled)
            {
                Machine.DisableSimdInstructions();
            }
            
            byte[] a = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0");
            byte[] b = Bytes.FromHexString("0xff0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            byte[] result = Bytes.FromHexString("0x0fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
            
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.XOR)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            var receipt = Execute(code);
            AssertSimd(receipt, result);
        }
        
        [Test]
        public void Not()
        {
            if (_simdDisabled)
            {
                Machine.DisableSimdInstructions();
            }
            
            byte[] a = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0");
            byte[] result = Bytes.FromHexString("0x0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(a) // just to make gas usage same as in other tests
                .Op(Instruction.NOT)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertSimd(receipt, result);
        }
        
        private void AssertSimd(TestAllTracerWithOutput receipt, string result)
        {
            AssertSimd(receipt, Bytes.FromHexString(result));
        }
        
        private void AssertSimd(TestAllTracerWithOutput receipt, ReadOnlySpan<byte> result)
        {
            AssertStorage(0, result);
            AssertGas(receipt, result.IsZero() ? ZeroResultGas : NonZeroResultGas);
        }
        
        private const long ZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SStoreNetMeteredEip1283;
        private const long NonZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet;
    }
}
