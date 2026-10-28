// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.All)]
public class OpcodeDefinitionTests
{
    private static readonly object[] Cases =
    [
        new TestCaseData(Instruction.ADD, Frontier.Instance, true).SetName("ADD is defined from the start"),
        new TestCaseData(Instruction.INVALID, Osaka.Instance, true).SetName("INVALID is the designated invalid operation"),
        new TestCaseData((Instruction)0x0d, Osaka.Instance, false).SetName("0x0d is unassigned"),
        new TestCaseData(Instruction.PUSH0, London.Instance, false).SetName("PUSH0 is undefined before Shanghai"),
        new TestCaseData(Instruction.PUSH0, Shanghai.Instance, true).SetName("PUSH0 is defined from Shanghai"),
        new TestCaseData(Instruction.APPROVE, Osaka.Instance, false).SetName("APPROVE is undefined without frame transactions"),
        new TestCaseData(Instruction.APPROVE, Eip8141Prototype.Instance, true).SetName("APPROVE is defined with frame transactions"),
    ];

    [TestCaseSource(nameof(Cases))]
    public void IsDefined_follows_the_spec(Instruction opcode, IReleaseSpec spec, bool expected) =>
        Assert.That(VirtualMachine<EthereumGasPolicy>.IsDefined(opcode, spec), Is.EqualTo(expected), $"{opcode} under {spec.Name}");
}
