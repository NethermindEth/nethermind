// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using Nethermind.Evm.Precompiles.Snarks;
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
            byte[] code = Prepare.EvmCode
                .CallWithInput(Bn254AddPrecompile.Instance.Address, 1000L, new byte[128])
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 500);
        }

        [Test]
        public void Test_add_after_istanbul()
        {
            byte[] code = Prepare.EvmCode
                .CallWithInput(Bn254AddPrecompile.Instance.Address, 1000L, new byte[128])
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 150);
        }

        [Test]
        public void Test_mul_before_istanbul()
        {
            _blockNumberAdjustment = -1;
            byte[] code = Prepare.EvmCode
                .CallWithInput(Bn254MulPrecompile.Instance.Address, 50000L, new byte[128])
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 40000L);
        }

        [Test]
        public void Test_mul_after_istanbul()
        {
            byte[] code = Prepare.EvmCode
                .CallWithInput(Bn254MulPrecompile.Instance.Address, 10000L, new byte[128])
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 6000L);
        }

        [Test]
        public void Test_pairing_before_istanbul()
        {
            _blockNumberAdjustment = -1;
            byte[] code = Prepare.EvmCode
                .CallWithInput(Bn254PairingPrecompile.Instance.Address, 200000L, new byte[192])
                .Done;
            TestAllTracerWithOutput result = Execute(BlockNumber, 1000000L, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 100000L + 80000L);
        }

        [Test]
        public void Test_pairing_after_istanbul()
        {
            byte[] code = Prepare.EvmCode
                .CallWithInput(Bn254PairingPrecompile.Instance.Address, 200000L, new byte[192])
                .Done;
            TestAllTracerWithOutput result = Execute(BlockNumber, 1000000L, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 45000L + 34000L);
        }
    }
}
