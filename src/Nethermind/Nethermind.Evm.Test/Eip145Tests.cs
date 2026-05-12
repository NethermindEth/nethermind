// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip145Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => 1;

        protected override ISpecProvider SpecProvider => new CustomSpecProvider(
            ((ForkActivation)0, Byzantium.Instance), ((ForkActivation)1, Constantinople.Instance));

        private void AssertEip145(TestAllTracerWithOutput receipt, string result) => AssertEip145(receipt, Bytes.FromHexString(result));

        private void AssertEip145(TestAllTracerWithOutput receipt, ReadOnlySpan<byte> result)
        {
            AssertStorage(0, result);
            AssertGas(receipt, result.IsZero() ? ZeroResultGas : NonZeroResultGas);
        }

        private const long ZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SStoreNetMeteredEip1283;
        private const long NonZeroResultGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet;

        // SHL test cases
        [TestCase(Instruction.SHL, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x00", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SHL, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000002")]
        [TestCase(Instruction.SHL, "0x0000000000000000000000000000000000000000000000000000000000000001", "0xff", "0x8000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHL, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHL, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x0101", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHL, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x00", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SHL, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe")]
        [TestCase(Instruction.SHL, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0x8000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHL, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHL, "0x0000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHL, "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe")]
        // SHR test cases
        [TestCase(Instruction.SHR, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x00", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SHR, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x4000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0xff", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SHR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0x0101", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x00", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SHR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SHR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SHR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SHR, "0x0000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        // SAR test cases
        [TestCase(Instruction.SAR, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x00", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SAR, "0x0000000000000000000000000000000000000000000000000000000000000001", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SAR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0x01", "0xc000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SAR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0xff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0x0100", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0x8000000000000000000000000000000000000000000000000000000000000000", "0x0101", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x00", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x01", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x100", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        [TestCase(Instruction.SAR, "0x0000000000000000000000000000000000000000000000000000000000000000", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SAR, "0x4000000000000000000000000000000000000000000000000000000000000000", "0xfe", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SAR, "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xf8", "0x000000000000000000000000000000000000000000000000000000000000007f")]
        [TestCase(Instruction.SAR, "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xfe", "0x0000000000000000000000000000000000000000000000000000000000000001")]
        [TestCase(Instruction.SAR, "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xff", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase(Instruction.SAR, "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x0100", "0x0000000000000000000000000000000000000000000000000000000000000000")]
        public void Shift_operations(Instruction instruction, string a, string b, string result)
        {
            byte[] code = Prepare.EvmCode
                .PushData(a)
                .PushData(b)
                .Op(instruction)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            AssertEip145(receipt, result);
        }
    }
}
