// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8163: the <c>EXTENSION (0xae)</c> opcode is reserved. On L1 it must keep
/// behaving exactly like <c>INVALID</c> — exceptional halt consuming all remaining gas — and
/// must not affect JUMPDEST analysis (no immediates).
/// </summary>
[TestFixture]
public class Eip8163Tests : VirtualMachineTestsBase
{
    private const byte Extension = 0xae;

    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(Amsterdam.Instance);

    [Test]
    public void Extension_halts_exceptionally_consuming_all_gas()
    {
        const ulong gasLimit = 100000;
        byte[] code = [Extension];

        TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(result.GasSpent, Is.EqualTo(gasLimit), "EXTENSION must consume all gas like INVALID");
        }
    }

    [Test]
    public void Extension_byte_does_not_affect_jumpdest_analysis()
    {
        // PUSH1 0x04 JUMP | 0xae | JUMPDEST STOP — the JUMPDEST directly after the reserved
        // byte must stay a valid destination, i.e. 0xae must not be treated as carrying
        // immediates.
        byte[] code = [(byte)Instruction.PUSH1, 0x04, (byte)Instruction.JUMP, Extension, (byte)Instruction.JUMPDEST, (byte)Instruction.STOP];

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
    }
}
