// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockProcessingPauseGateTests
{
    [TestCase(Operation.Pause, false, TestName = "Pause from running transitions to paused")]
    [TestCase(Operation.Pause, true, TestName = "Pause while already paused is a no-op")]
    [TestCase(Operation.Resume, true, TestName = "Resume from paused transitions to running")]
    [TestCase(Operation.Resume, false, TestName = "Resume while already running is a no-op")]
    public void Transition_reportsWhetherStateChanged(Operation operation, bool startPaused)
    {
        BlockProcessingPauseGate gate = new();
        if (startPaused) gate.Pause();

        bool pausing = operation == Operation.Pause;
        bool transitioned = pausing ? gate.Pause() : gate.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.IsPaused, Is.EqualTo(pausing), "Pause ends paused; Resume ends running");
            Assert.That(transitioned, Is.EqualTo(startPaused != pausing), "a transition happens only when the resulting state differs from the start");
        }
    }

    [Test]
    public void WaitWhilePausedAsync_whenRunning_completesSynchronously()
    {
        BlockProcessingPauseGate gate = new();

        ValueTask wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        Assert.That(wait.IsCompleted, Is.True, "the loop must not park when the gate is running");
    }

    [Test]
    public async Task WaitWhilePausedAsync_whenPaused_parksUntilResumed()
    {
        BlockProcessingPauseGate gate = new();
        gate.Pause();

        ValueTask parked = gate.WaitWhilePausedAsync(CancellationToken.None);
        Assert.That(parked.IsCompleted, Is.False, "the loop must park while the gate is paused");

        gate.Resume();

        await parked;
    }

    [Test]
    public void WaitWhilePausedAsync_whenCancelledWhilePaused_throwsOperationCanceled()
    {
        BlockProcessingPauseGate gate = new();
        gate.Pause();
        using CancellationTokenSource cts = new();

        ValueTask parked = gate.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();

        Assert.That(async () => await parked, Throws.InstanceOf<OperationCanceledException>());
    }

    public enum Operation
    {
        Pause,
        Resume
    }
}
