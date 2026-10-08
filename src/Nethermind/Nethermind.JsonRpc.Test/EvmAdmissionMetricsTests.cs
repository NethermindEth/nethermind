// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using NUnit.Framework;
using static Nethermind.JsonRpc.EvmAdmissionGate;

namespace Nethermind.JsonRpc.Test;

// The RpcAdmission gauges are process-wide sums that every gate changes by deltas, so a change it misses drifts for good.
// Run alone, so that no other gate moves them meanwhile.
[NonParallelizable]
public class EvmAdmissionMetricsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Gauges_return_to_where_they_were_after_every_outcome()
    {
        (long inFlightBefore, long queuedBefore) = (Metrics.RpcAdmissionInFlight, Metrics.RpcAdmissionQueued);
        ManualClock clock = new();
        EvmAdmissionGate gate = new(
            new JsonRpcConfig { EthModuleConcurrentInstances = 1, EvmExecutionMaxQueueWaitMs = 1_000, EvmExecutionQueueLimit = 3 }, clock);

        Lease held = await gate.AdmitAsync(gate.Budget, CancellationToken.None);
        Task<Lease> late = gate.AdmitAsync(gate.Budget, CancellationToken.None).AsTask();
        using CancellationTokenSource cancellation = new();
        Task<Lease> cancelled = gate.AdmitAsync(gate.Budget, cancellation.Token).AsTask();
        Task<Lease> timedOut = gate.AdmitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None).AsTask();
        Assert.That(async () => await gate.AdmitAsync(gate.Budget, CancellationToken.None), Throws.InstanceOf<LimitExceededException>(), "queue full");
        Assert.That(async () => await gate.AdmitAsync(TimeSpan.Zero, CancellationToken.None), Throws.InstanceOf<LimitExceededException>(), "may not wait");

        cancellation.Cancel();
        Assert.That(async () => await cancelled.WaitAsync(TestTimeout), Throws.InstanceOf<OperationCanceledException>());
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.That(async () => await timedOut.WaitAsync(TestTimeout), Throws.TypeOf<WaitTimeoutException>());

        // The next waiter arrives at 10 ms; at 1,005 ms the first one's wait has ended but its timer has not fired.
        Task<Lease> granted = gate.AdmitAsync(gate.Budget, CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(995), fireTimers: false);
        held.Dispose();
        Assert.That(async () => await late.WaitAsync(TestTimeout), Throws.TypeOf<WaitTimeoutException>());
        (await granted.WaitAsync(TestTimeout)).Dispose();

        Assert.That(
            (Metrics.RpcAdmissionInFlight - inFlightBefore, Metrics.RpcAdmissionQueued - queuedBefore, gate.InFlight, gate.Queued),
            Is.EqualTo((0L, 0L, 0, 0)));
    }
}
