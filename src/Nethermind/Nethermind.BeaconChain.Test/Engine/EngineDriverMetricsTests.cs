// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Engine;

/// <summary>Asserts exact deltas of process-wide counters, so it must not overlap any other fixture.</summary>
[NonParallelizable]
public class EngineDriverMetricsTests
{
    private const int Threads = 8;
    private const int CallsPerThread = 100_000;

    /// <summary>
    /// Engine calls run on the slot worker and on other threads at once, so a plain read-modify-write of a shared counter loses
    /// counts; every call must be counted.
    /// </summary>
    [Test]
    [HardTimeout(300_000)]
    public void Engine_calls_made_from_many_threads_at_once_are_all_counted()
    {
        (IEngineRpcModule Engine, EngineDriver Driver)[] drivers = [.. Enumerable.Range(0, Threads).Select(static _ => CreateDriver())];
        ExecutionPayloadGloas payload = EngineTests.GloasPayload(5);
        ulong newPayloadBefore = Metrics.BeaconChainNewPayloadCalls;
        ulong forkchoiceBefore = Metrics.BeaconChainForkchoiceUpdatedCalls;
        using Barrier start = new(Threads);
        int failedThreads = 0;

        Thread[] threads = [.. drivers.Select(pair => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < CallsPerThread; i++)
                {
                    pair.Driver.NotifyNewPayload(payload, [], TestItem.KeccakA, new ExecutionRequestsGloas());
                    pair.Driver.ForkchoiceUpdated(TestItem.KeccakA, TestItem.KeccakA, TestItem.KeccakA).GetAwaiter().GetResult();
                    // The substitute records every call; clearing keeps memory flat over the run.
                    if (i % 10_000 == 0) pair.Engine.ClearReceivedCalls();
                }
            }
            catch
            {
                Interlocked.Increment(ref failedThreads);
            }
        }))];
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failedThreads, Is.Zero, "every call must return a verdict");
            Assert.That(Metrics.BeaconChainNewPayloadCalls - newPayloadBefore, Is.EqualTo((ulong)(Threads * CallsPerThread)));
            Assert.That(Metrics.BeaconChainForkchoiceUpdatedCalls - forkchoiceBefore, Is.EqualTo((ulong)(Threads * CallsPerThread)));
        }
    }

    private static (IEngineRpcModule Engine, EngineDriver Driver) CreateDriver()
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        EngineTests.ConfigureEngine(engine, static () => Task.FromResult(PayloadStatusV1.Syncing));
        return (engine, TestEngineDriver.Create(EngineTests.CreateDetector(engine, out _)));
    }
}
