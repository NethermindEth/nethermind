// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockProcessingPauseGateTests
{
    [Test]
    public void Pause_whenRunning_transitionsToPausedAndIsIdempotent()
    {
        BlockProcessingPauseGate gate = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.IsPaused, Is.False, "a fresh gate starts running");
            Assert.That(gate.Pause(), Is.True, "the first pause transitions from running to paused");
            Assert.That(gate.Pause(), Is.False, "pausing an already-paused gate reports no transition");
            Assert.That(gate.IsPaused, Is.True, "the gate remains paused");
        }
    }

    [Test]
    public void Resume_whenPaused_transitionsToRunningAndIsIdempotent()
    {
        BlockProcessingPauseGate gate = new();
        gate.Pause();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.Resume(), Is.True, "resuming a paused gate transitions to running");
            Assert.That(gate.Resume(), Is.False, "resuming an already-running gate reports no transition");
            Assert.That(gate.IsPaused, Is.False, "the gate remains running");
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

        await parked; // completes deterministically once resumed; no timing window
        Assert.That(gate.IsPaused, Is.False, "the gate is running after resume");
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
