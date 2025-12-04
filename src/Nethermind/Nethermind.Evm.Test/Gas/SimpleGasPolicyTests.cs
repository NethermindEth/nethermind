// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.Evm;
using Nethermind.Evm.Gas;

namespace Nethermind.Evm.Test.Gas;

[TestFixture]
public class SimpleGasPolicyTests
{
    [Test]
    public void SimpleGasPolicy_ConsumeGas_ReducesRemainingGas()
    {
        var gasState = new GasState(1000000);

        SimpleGasPolicy.ConsumeGas(ref gasState, 3, Instruction.ADD);

        Assert.That(gasState.RemainingGas, Is.EqualTo(999997));
    }

    [Test]
    public void SimpleGasPolicy_ConsumeGas_IgnoresInstruction()
    {
        var gasState1 = new GasState(1000000);
        var gasState2 = new GasState(1000000);

        SimpleGasPolicy.ConsumeGas(ref gasState1, 100, Instruction.SSTORE);
        SimpleGasPolicy.ConsumeGas(ref gasState2, 100, Instruction.ADD);

        // Same gas cost, different instructions - same result for simple policy
        Assert.That(gasState1.RemainingGas, Is.EqualTo(gasState2.RemainingGas));
    }
}
