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
    public void SimpleGasPolicy_InitializeForTransaction_SetsRemainingGas()
    {
        long gasLimit = 1000000;
        long intrinsicGas = 21000;

        GasState gasState = SimpleGasPolicy.InitializeForTransaction(gasLimit, intrinsicGas);

        Assert.That(gasState.RemainingGas, Is.EqualTo(gasLimit));
        Assert.That(gasState.PolicyData, Is.Null);
    }

    [Test]
    public void SimpleGasPolicy_GetRemainingGas_ReturnsCorrectValue()
    {
        var gasState = new GasState(500000);

        var remaining = SimpleGasPolicy.GetRemainingGas(in gasState);

        Assert.That(remaining, Is.EqualTo(500000));
    }

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

    [Test]
    public void SimpleGasPolicy_ConsumeGas_CanGoNegative()
    {
        var gasState = new GasState(10);

        SimpleGasPolicy.ConsumeGas(ref gasState, 20, Instruction.ADD);

        Assert.That(gasState.RemainingGas, Is.EqualTo(-10));
    }

    [Test]
    public void SimpleGasPolicy_InitializeChildFrame_CreatesNewGasState()
    {
        long gasProvided = 50000;

        GasState childState = SimpleGasPolicy.InitializeChildFrame(gasProvided);

        Assert.That(childState.RemainingGas, Is.EqualTo(50000));
        Assert.That(childState.PolicyData, Is.Null);
    }

    [Test]
    public void SimpleGasPolicy_MergeChildFrame_AddsReturnedGas()
    {
        var parentState = new GasState(950000); // After providing 50000 to child
        var childState = new GasState(30000); // Child used 20000 of 50000
        long gasProvided = 50000;

        SimpleGasPolicy.MergeChildFrame(
            ref parentState,
            in childState,
            gasProvided);

        Assert.That(parentState.RemainingGas, Is.EqualTo(980000)); // 950000 + 30000
    }

    [Test]
    public void SimpleGasPolicy_GetFinalGasUsed_ReturnsCorrectValue()
    {
        var gasState = new GasState(300000);
        long gasLimit = 1000000;

        var gasUsed = SimpleGasPolicy.GetFinalGasUsed(in gasState, gasLimit);

        Assert.That(gasUsed, Is.EqualTo(700000));
    }

    [Test]
    public void SimpleGasPolicy_GetReceiptData_ReturnsNull()
    {
        var gasState = new GasState(500000);

        var receiptData = SimpleGasPolicy.GetReceiptData(in gasState);

        Assert.That(receiptData, Is.Null);
    }

    [Test]
    public void SimpleGasPolicy_ApplyRefund_NoOp()
    {
        var gasState = new GasState(1000000);

        SimpleGasPolicy.ApplyRefund(ref gasState, 5000);

        // Refunds don't affect GasState for simple policy
        Assert.That(gasState.RemainingGas, Is.EqualTo(1000000));
    }
}
