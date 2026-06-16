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
    [TestCase(false, true, true, true, TestName = "Pause from running transitions to paused")]
    [TestCase(true, true, false, true, TestName = "Pause while already paused is a no-op")]
    [TestCase(true, false, true, false, TestName = "Resume from paused transitions to running")]
    [TestCase(false, false, false, false, TestName = "Resume while already running is a no-op")]
    public void Transition_reportsWhetherStateChanged(bool startPaused, bool pause, bool expectedTransitioned, bool expectedPausedAfter)
    {
        BlockProcessingPauseGate gate = new();
        if (startPaused) gate.Pause();

        bool transitioned = pause ? gate.Pause() : gate.Resume();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transitioned, Is.EqualTo(expectedTransitioned), "the call reports whether it changed the state");
            Assert.That(gate.IsPaused, Is.EqualTo(expectedPausedAfter), "the resulting paused state");
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
}
