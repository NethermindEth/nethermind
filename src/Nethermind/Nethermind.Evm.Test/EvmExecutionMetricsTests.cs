// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
[NonParallelizable]
public class EvmExecutionMetricsTests : VirtualMachineTestsBase
{
    [Test]
    public void Sload_and_sstore_counts_are_flushed_to_the_metrics()
    {
        long sstoreBefore = Metrics.SstoreOpcode;
        long sloadBefore = Metrics.SloadOpcode;

        byte[] code = Prepare.EvmCode
            .PushData(0x11).PushData(0x01).Op(Instruction.SSTORE)
            .PushData(0x22).PushData(0x02).Op(Instruction.SSTORE)
            .PushData(0x01).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(0x02).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(0x03).Op(Instruction.SLOAD).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        Execute(code);

        Assert.That(Metrics.SstoreOpcode - sstoreBefore, Is.EqualTo(2));
        Assert.That(Metrics.SloadOpcode - sloadBefore, Is.EqualTo(3));
    }
}
