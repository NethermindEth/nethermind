// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class CmpTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ConstantinopleFixBlockNumber;

        private void AssertEip1014(Address address, byte[] code)
        {
            AssertCodeHash(address, Keccak.Compute(code));
        }

        [Test]
        public void Gt()
        {
            byte[] a = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0ff");
            byte[] b = Bytes.FromHexString("0x0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            byte[] result = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000");

            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.GT)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertCmp(receipt, result);
        }

        [Test]
        public void Lt()
        {
            byte[] a = Bytes.FromHexString("0x0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            byte[] b = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0");
            byte[] result = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000");

            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.LT)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertCmp(receipt, result);
        }

        [Test]
        public void Eq()
        {
            byte[] a = Bytes.FromHexString("0xf0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0");
            byte[] b = Bytes.FromHexString("0x0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");
            byte[] result = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000");

            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(Instruction.EQ)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertCmp(receipt, result);
        }

        private void AssertCmp(TestAllTracerWithOutput receipt, string result)
        {
            AssertCmp(receipt, Bytes.FromHexString(result));
        }

        private void AssertCmp(TestAllTracerWithOutput receipt, byte[] result)
        {
            AssertStorage(0, result);
            AssertGas(receipt, result.IsZero() ? ZeroResultGas : NonZeroResultGas);
        }

        private const long ZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset;
        private const long NonZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet;
    }
}
