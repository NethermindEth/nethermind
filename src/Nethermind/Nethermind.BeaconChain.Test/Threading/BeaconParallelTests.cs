// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.Crypto;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Threading;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Threading;

public class BeaconParallelTests
{
    /// <summary>
    /// A loop on the import thread whose helpers sit in the thread-pool queue waits as long as that queue, which is minutes
    /// while the pool is starved; the helpers of these loops never reach the pool.
    /// </summary>
    [Test]
    public void Loop_bodies_started_from_a_dedicated_thread_never_run_on_pool_threads()
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));
        ConcurrentBag<(int Thread, bool Pool)> bodies = [];
        RunOnDedicatedThread(() => BeaconParallel.For(0, 64, _ =>
        {
            bodies.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
            Thread.Sleep(1);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(64));
            Assert.That(bodies.Where(static body => body.Pool), Is.Empty, "a loop body ran on a thread-pool thread");
            Assert.That(bodies.Select(static body => body.Thread).Distinct().Count(), Is.GreaterThan(1), "the loop did not fan out");
        });
    }

    [Test]
    public void Nested_loops_from_concurrent_callers_complete_when_they_hold_every_compute_thread()
    {
        const int callers = 4;
        int outer = Environment.ProcessorCount * 2;
        int total = 0;
        RunOnDedicatedThreads(callers, () => BeaconParallel.For(0, outer, _ =>
            BeaconParallel.For(0, 16, _ =>
            {
                Interlocked.Increment(ref total);
                Thread.Sleep(1);
            })));

        Assert.That(total, Is.EqualTo(callers * outer * 16));
    }

    [Test]
    public void A_fault_in_a_body_reaches_the_caller() =>
        Assert.That(() => BeaconParallel.For(0, 64, static i =>
        {
            if (i == 37) throw new InvalidOperationException("body 37");
        }), Throws.InstanceOf<AggregateException>().With.InnerException.Message.EqualTo("body 37"));

    [TestCaseSource(nameof(LoopingCodePaths))]
    public void A_code_path_runs_its_loops_on_the_compute_threads(Func<Action> prepare)
    {
        Action codePath = prepare();
        int before = BeaconParallel.LoopsStartedOnThisThread;
        codePath();
        Assert.That(BeaconParallel.LoopsStartedOnThisThread, Is.GreaterThan(before));
    }

    // Each case prepares its fixture first, so only the loop under test runs while the count is watched.
    private static IEnumerable<TestCaseData> LoopingCodePaths()
    {
        yield return Case("Committee shuffle", static () =>
        {
            // Either mirror of a round spans at least a quarter of the list, above the parallel threshold of 1 << 16.
            int[] indices = [.. Enumerable.Range(0, 1 << 19)];
            return () => SwapOrNotShuffle.ShuffleList(indices, new byte[32], rounds: 1);
        });
        yield return Case("Chunk tree rebuild", static () =>
        {
            MerkleChunkTree tree = new(20);
            tree.SetLeafCount(1 << 14);
            return tree.Rebuild;
        });
        yield return Case("Chunk tree update", static () =>
        {
            MerkleChunkTree tree = new(20);
            tree.SetLeafCount(1 << 14);
            int[] dirty = [.. Enumerable.Range(0, 1 << 14)];
            return () => tree.Update(dirty, dirty.Length);
        });
        yield return Case("State hash of a registry with every validator replaced", static () =>
        {
            BeaconStateFulu state = CachedHasherTests.CreateState(2048);
            CachedBeaconStateHasher hasher = new();
            hasher.HashTreeRoot(state);
            state.Validators = [.. state.Validators!.Select(static validator => validator.Clone())];
            return () => hasher.HashTreeRoot(state);
        });
        yield return Case("Pubkey cache build", static () =>
        {
            Validator[] validators = Validators(2);
            return () => new PubkeyCache().Build(validators);
        });
        yield return Case("Pubkey subgroup checks of an aggregate", static () =>
        {
            PubkeyCache cache = new();
            cache.Build(Validators(64));
            ulong[] indices = [.. Enumerable.Range(0, 64).Select(static i => (ulong)i)];
            return () => cache.TrySumValidPublicKeys(indices, new long[Nethermind.Crypto.Bls.P1.Sz]);
        });
        yield return Case("Pubkey subgroup check warm-up", static () =>
        {
            PubkeyCache cache = new();
            cache.Build(Validators(2));
            return () => cache.WarmSubgroupChecks(CancellationToken.None);
        });

        static TestCaseData Case(string name, Func<Action> prepare) => new TestCaseData(prepare).SetName(name);
    }

    private static Validator[] Validators(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => new Validator { Pubkey = new BlsPublicKey(PubkeyCacheTests.CompressedPubkey(i)) })];

    private static void RunOnDedicatedThread(Action action) => RunOnDedicatedThreads(1, action);

    private static void RunOnDedicatedThreads(int count, Action action)
    {
        ConcurrentQueue<Exception> faults = [];
        Thread[] threads = [.. Enumerable.Range(0, count).Select(_ => new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                faults.Enqueue(e);
            }
        }))];
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) Assert.That(thread.Join(TimeSpan.FromMinutes(1)), Is.True, "a loop did not finish");
        if (faults.TryDequeue(out Exception? fault)) throw fault;
    }
}
