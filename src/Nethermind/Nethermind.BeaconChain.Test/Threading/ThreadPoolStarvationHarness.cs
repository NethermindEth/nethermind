// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Diagnostics;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Threading;

/// <summary>
/// Measures how block-import work on a dedicated thread and the thread pool behave under a synthetic load that has the shape of
/// an execution layer snap sync next to the beacon networking. Run by hand; it loads the whole process for about a minute.
/// </summary>
/// <remarks>
/// The load is a model, not the real execution layer: each "sync response" runs on a pool thread, queues helper work items to the
/// global pool queue and waits until every one is dequeued, as a parallel loop without a worker scope does; "pinned" pool threads
/// block on a queue for the whole run, as libp2p's yamux dial loop does per connection; "gossip" loops take one shared lock for CPU
/// work, as libp2p's pubsub router does around message validation.
/// </remarks>
[Explicit("Load harness; loads the whole process")]
[NonParallelizable]
public class ThreadPoolStarvationHarness
{
    private const int ShuffledValidators = 1 << 20;

    [TestCase(false, 0, 0, false, TestName = "Idle")]
    [TestCase(true, 0, 0, false, TestName = "Sync responses only")]
    [TestCase(false, 100, 40, false, TestName = "Pinned threads and gossip lock only")]
    [TestCase(true, 100, 0, false, TestName = "Sync responses and pinned threads")]
    [TestCase(true, 0, 40, false, TestName = "Sync responses and gossip lock")]
    [TestCase(true, 100, 40, false, TestName = "Sync responses, pinned threads and gossip lock")]
    [TestCase(true, 100, 40, true, TestName = "Sync responses, pinned threads and gossip lock, minimum threads raised by the pinned count")]
    public void Import_work_and_pool_latency_under_load(bool syncResponses, int pinnedThreads, int gossipPeers, bool raiseMinimumThreads)
    {
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        List<string> watchdogLines = [];
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.When(x => x.Info(Arg.Any<string>())).Do(call => { lock (watchdogLines) watchdogLines.Add(call.Arg<string>()); });
        using ProcessStallWatchdog watchdog = new(new OneLoggerLogManager(new ILogger(logger)));
        using CancellationTokenSource stop = new();
        List<Task> load = [];
        BlockingCollection<int> neverFed = [];
        try
        {
            if (raiseMinimumThreads) ThreadPool.SetMinThreads(minimumWorkers + pinnedThreads, minimumIo);
            watchdog.Start();
            for (int i = 0; i < pinnedThreads; i++)
            {
                load.Add(Task.Run(() =>
                {
                    try
                    {
                        foreach (int _ in neverFed.GetConsumingEnumerable(stop.Token)) { }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }));
            }

            object routerLock = new();
            for (int peer = 0; peer < gossipPeers; peer++)
            {
                int seed = peer;
                load.Add(Task.Run(async () =>
                {
                    Random random = new(seed);
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(random.Next(20, 100));
                        lock (routerLock) SpinFor(TimeSpan.FromMilliseconds(2));
                    }
                }));
            }

            if (syncResponses)
            {
                // Two sync feeds, each processing up to one response per processor at once.
                for (int feed = 0; feed < 2; feed++)
                {
                    load.Add(Task.Run(async () =>
                    {
                        using SemaphoreSlim processing = new(Environment.ProcessorCount);
                        while (!stop.IsCancellationRequested)
                        {
                            await processing.WaitAsync();
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    ParallelLoopWithoutScope(TimeSpan.FromMilliseconds(20));
                                }
                                finally
                                {
                                    processing.Release();
                                }
                            });
                        }
                    }));
                }
            }

            Thread.Sleep(TimeSpan.FromSeconds(5));
            List<double> importMs = [];
            List<double> probeMs = [];
            long maxPending = 0;
            int maxThreads = 0;
            Thread import = new(() =>
            {
                int[] indices = new int[ShuffledValidators];
                MerkleChunkTree tree = new(40);
                tree.SetLeafCount(ShuffledValidators);
                Stopwatch total = Stopwatch.StartNew();
                while (total.Elapsed < TimeSpan.FromSeconds(30))
                {
                    long started = Stopwatch.GetTimestamp();
                    SwapOrNotShuffle.ShuffleList(indices, new byte[32]);
                    tree.Rebuild();
                    importMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            })
            { IsBackground = true, Name = "Harness import" };
            import.Start();
            while (import.IsAlive)
            {
                long queued = Stopwatch.GetTimestamp();
                using ManualResetEventSlim ran = new();
                ThreadPool.UnsafeQueueUserWorkItem(static state => state.Set(), ran, preferLocal: false);
                if (!ran.Wait(TimeSpan.FromSeconds(60))) Assert.Fail("probe did not run within 60 s");
                probeMs.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
                maxPending = Math.Max(maxPending, ThreadPool.PendingWorkItemCount);
                maxThreads = Math.Max(maxThreads, ThreadPool.ThreadCount);
                Thread.Sleep(250);
            }

            Report($"{TestContext.CurrentContext.Test.Name}: cores {Environment.ProcessorCount}");
            Report($"  import (1M shuffle + 1M-leaf rebuild): {importMs.Count} runs, median {Median(importMs):F0} ms, max {importMs.Max():F0} ms");
            Report($"  pool probe latency: median {Median(probeMs):F0} ms, p95 {Percentile(probeMs, 0.95):F0} ms, max {probeMs.Max():F0} ms");
            Report($"  max pending work items {maxPending}, max pool threads {maxThreads}");
            lock (watchdogLines)
            {
                Report($"  watchdog lines: {watchdogLines.Count}");
                foreach (string line in watchdogLines.Take(6)) Report($"    {line}");
            }
        }
        finally
        {
            stop.Cancel();
            neverFed.CompleteAdding();
            Task.WaitAll([.. load], TimeSpan.FromSeconds(60));
            ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
        }
    }

    // One helper per processor goes to the global queue; the caller claims the whole range and waits until every helper is dequeued.
    private static void ParallelLoopWithoutScope(TimeSpan work)
    {
        int helpers = Environment.ProcessorCount - 1;
        using CountdownEvent dequeued = new(helpers);
        for (int i = 0; i < helpers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Signal(), dequeued, preferLocal: false);
        }

        SpinFor(work);
        dequeued.Wait();
    }

    private static void SpinFor(TimeSpan duration)
    {
        long end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            Thread.SpinWait(64);
        }
    }

    // The test host does not always print test output, so the lines also go to a file next to the test assembly.
    private static void Report(string line)
    {
        TestContext.Out.WriteLine(line);
        File.AppendAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "starvation-harness.txt"), line + Environment.NewLine);
    }

    private static double Median(List<double> values) => Percentile(values, 0.5);

    private static double Percentile(List<double> values, double fraction)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(fraction * sorted.Length))];
    }
}
