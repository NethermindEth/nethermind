// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Evm.Test;

[NonParallelizable]
public class ExecutionMetricsBatchingTests : VirtualMachineTestsBase
{
    [Test]
    public void Per_opcode_counters_flush_exact_totals()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .PushData(1).PushData(1).Op(Instruction.SSTORE)
            .PushData(1).PushData(2).Op(Instruction.SSTORE)
            .PushData(2).PushData(0).Op(Instruction.SSTORE)
            .PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(1).Op(Instruction.SLOAD).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        long sstoreBefore = Metrics.SstoreOpcode;
        long sloadBefore = Metrics.SloadOpcode;

        Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.SstoreOpcode - sstoreBefore, Is.EqualTo(4), "SSTORE count");
            Assert.That(Metrics.SloadOpcode - sloadBefore, Is.EqualTo(2), "SLOAD count");
        }
    }
}
