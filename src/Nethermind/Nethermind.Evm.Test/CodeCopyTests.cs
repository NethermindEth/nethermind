// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CodeCopyTests : VirtualMachineTestsBase
    {
        [Test]
        public void CodeCopy_ZeroLength_ConsumesBaseGas()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.CODECOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            result.Error.Should().BeNull();
            long expectedGas = GasCostOf.Transaction + 3 * GasCostOf.VeryLow + GasCostOf.VeryLow;
            result.GasSpent.Should().Be(expectedGas);
        }

        [Test]
        public void CodeCopy_ZeroLength_InsufficientGas_ReturnsOutOfGas()
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.CODECOPY)
                .Done;

            long expectedGas = GasCostOf.Transaction + 3 * GasCostOf.VeryLow + GasCostOf.VeryLow;
            long gasLimit = expectedGas - 1;

            TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);

            result.Error.Should().Be("OutOfGas");
        }

        [Test]
        public void CodeCopy_OneWord_ConsumesMemoryGas()
        {
            byte[] code = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.CODECOPY)
                .Done;

            TestAllTracerWithOutput result = Execute(code);

            result.Error.Should().BeNull();
            long expectedGas = GasCostOf.Transaction + 3 * GasCostOf.VeryLow + GasCostOf.VeryLow + GasCostOf.Memory + GasCostOf.Memory;
            result.GasSpent.Should().Be(expectedGas);
        }
    }
}
