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

using Nethermind.Specs;
using Nethermind.Evm.Precompiles.Snarks.Shamatar;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1108Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber + _blockNumberAdjustment;

        private int _blockNumberAdjustment;

        [TearDown]
        public void TearDown()
        {
            _blockNumberAdjustment = 0;
        }

        [Test]
        public void Test_add_before_istanbul()
        {
            _blockNumberAdjustment = -1;
            var code = Prepare.EvmCode
                .CallWithInput(Bn256AddPrecompile.Instance.Address, 1000L, new byte[128])
                .Done;
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 500);
        }

        [Test]
        public void Test_add_after_istanbul()
        {
            var code = Prepare.EvmCode
                .CallWithInput(Bn256AddPrecompile.Instance.Address, 1000L, new byte[128])
                .Done;
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 150);
        }

        [Test]
        public void Test_mul_before_istanbul()
        {
            _blockNumberAdjustment = -1;
            var code = Prepare.EvmCode
                .CallWithInput(Bn256MulPrecompile.Instance.Address, 50000L, new byte[128])
                .Done;
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 40000L);
        }

        [Test]
        public void Test_mul_after_istanbul()
        {
            var code = Prepare.EvmCode
                .CallWithInput(Bn256MulPrecompile.Instance.Address, 10000L, new byte[128])
                .Done;
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 6000L);
        }

        [Test]
        public void Test_pairing_before_istanbul()
        {
            _blockNumberAdjustment = -1;
            var code = Prepare.EvmCode
                .CallWithInput(Bn256PairingPrecompile.Instance.Address, 200000L, new byte[192])
                .Done;
            var result = Execute(BlockNumber, 1000000L, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 100000L + 80000L);
        }

        [Test]
        public void Test_pairing_after_istanbul()
        {
            var code = Prepare.EvmCode
                .CallWithInput(Bn256PairingPrecompile.Instance.Address, 200000L, new byte[192])
                .Done;
            var result = Execute(BlockNumber, 1000000L, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 45000L + 34000L);
        }
    }
}
