// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [Parallelizable(ParallelScope.Self)]
    public class SimdTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ConstantinopleFixBlockNumber;

        [Test]
        public void And()
        {
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

            TestAllTracerWithOutput receipt = Execute(code);
            AssertSimd(receipt, result);
        }

        [Test]
        public void Or()
        {
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

            TestAllTracerWithOutput receipt = Execute(code);
            AssertSimd(receipt, result);
        }

        [Test]
        public void Xor()
        {
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

            TestAllTracerWithOutput receipt = Execute(code);
            AssertSimd(receipt, result);
        }

        [Test]
        public void Not()
        {
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

        private void AssertSimd(TestAllTracerWithOutput receipt, ReadOnlySpan<byte> result)
        {
            AssertStorage(0, result);
            AssertGas(receipt, result.IsZero() ? ZeroResultGas : NonZeroResultGas);
        }

        private const long ZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SStoreNetMeteredEip1283;
        private const long NonZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet;
    }
}
