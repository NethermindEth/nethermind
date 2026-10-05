// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Test.Builders;

namespace Nethermind.BeaconChain.Test.Engine;

// Exact process-wide counter deltas require fixture isolation.
[NonParallelizable]
public class EngineDriverMetricsTests
{
    private const int Threads = 8;
    private const int CallsPerThread = 10_000;

    [Test]
    [HardTimeout(60_000)]
    public void Engine_calls_made_from_many_threads_at_once_are_all_counted()
    {
        EngineDriver[] drivers = [.. Enumerable.Range(0, Threads).Select(static worker => EngineTests.CreateSyncingDriver(out _))];
        ExecutionPayloadGloas payload = EngineTests.GloasPayload(5);
        ulong newPayloadBefore = Metrics.BeaconChainNewPayloadCalls;
        ulong forkchoiceBefore = Metrics.BeaconChainForkchoiceUpdatedCalls;
        using Barrier start = new(Threads);
        int failedThreads = 0;

        Thread[] threads = [.. drivers.Select(driver => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < CallsPerThread; i++)
                {
                    driver.NotifyNewPayload(payload, [], TestItem.KeccakA, new ExecutionRequestsGloas());
                    driver.ForkchoiceUpdated(TestItem.KeccakA, TestItem.KeccakA, TestItem.KeccakA).GetAwaiter().GetResult();
                }
            }
            catch
            {
                Interlocked.Increment(ref failedThreads);
            }
        }))];
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(failedThreads, Is.Zero, "every call must return a verdict");
        Assert.That(Metrics.BeaconChainNewPayloadCalls - newPayloadBefore, Is.EqualTo((ulong)(Threads * CallsPerThread)));
        Assert.That(Metrics.BeaconChainForkchoiceUpdatedCalls - forkchoiceBefore, Is.EqualTo((ulong)(Threads * CallsPerThread)));
    }
}
