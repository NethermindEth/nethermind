// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class StackUnderflowRegressionTests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    // Each case leaves the stack exactly one item short of what the opcode's converted pop needs:
    // the preceding pops succeed, then the value/topic/salt pop underflows.
    private static readonly object[] UnderflowCases =
    [
        new object[] { Instruction.BYTE, Prepare.EvmCode.PushData(0).Op(Instruction.BYTE).Done },
        new object[] { Instruction.SSTORE, Prepare.EvmCode.PushData(0).Op(Instruction.SSTORE).Done },
        new object[] { Instruction.TSTORE, Prepare.EvmCode.PushData(0).Op(Instruction.TSTORE).Done },
        new object[] { Instruction.LOG1, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.LOG1).Done },
        new object[] { Instruction.CREATE2, Prepare.EvmCode.PushData(0).PushData(0).PushData(0).Op(Instruction.CREATE2).Done },
    ];

    [TestCaseSource(nameof(UnderflowCases))]
    public void Signals_stack_underflow_when_final_operand_missing(Instruction opcode, byte[] code)
    {
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.Error, Is.EqualTo(EvmExceptionType.StackUnderflow.ToString()), opcode.ToString());
    }
}
