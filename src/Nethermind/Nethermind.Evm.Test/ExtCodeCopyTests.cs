// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class ExtCodeCopyTests : VirtualMachineTestsBase
    {
        [Test]
        public void Ranges()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .Op(Instruction.EXTCODECOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.Error.Should().BeNull();
        }

        [Test]
        public void ExtCodeCopy_ZeroLength_ConsumesBaseGas()
        {
            Address target = TestItem.AddressC;

            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(target)
                .Op(Instruction.EXTCODECOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            result.Error.Should().BeNull();
            long expectedGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.ExtCodeEip150;
            result.GasSpent.Should().Be(expectedGas);
        }

        [Test]
        public void ExtCodeCopy_ZeroLength_InsufficientGas_ReturnsOutOfGas()
        {
            Address target = TestItem.AddressC;

            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(target)
                .Op(Instruction.EXTCODECOPY)
                .Done;

            long expectedGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.ExtCodeEip150;
            long gasLimit = expectedGas - 1;

            TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);

            result.Error.Should().Be("OutOfGas");
        }

        [Test]
        public void ExtCodeCopy_OneWord_ConsumesMemoryGas()
        {
            Address target = TestItem.AddressC;

            byte[] code = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .PushData(target)
                .Op(Instruction.EXTCODECOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            result.Error.Should().BeNull();
            long expectedGas = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.ExtCodeEip150 + GasCostOf.Memory + GasCostOf.Memory;
            result.GasSpent.Should().Be(expectedGas);
        }
    }
}
