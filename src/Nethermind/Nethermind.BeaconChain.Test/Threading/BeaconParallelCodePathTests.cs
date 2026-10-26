// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.Crypto;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Test.Threading;

[NonParallelizable]
public class BeaconParallelCodePathTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestCaseSource(nameof(LoopingCodePaths))]
    public void A_code_path_finishes_from_a_dedicated_thread_while_every_pool_thread_is_busy(Func<Action> prepare)
    {
        Action codePath = prepare();
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out int maximumIo);
        int workers = Math.Max(Environment.ProcessorCount, Math.Max(minimumWorkers, ThreadPool.ThreadCount));
        using ManualResetEventSlim release = new();
        using CountdownEvent finished = new(workers + 1);
        Thread? path = null;
        Exception? fault = null;
        try
        {
            Assert.That(ThreadPool.SetMaxThreads(workers, maximumIo) && ThreadPool.SetMinThreads(workers, minimumIo), Is.True, "fixture");
            // One more blocker than the pool may have threads: once it stays queued at the cap, no pool thread is free.
            for (int i = 0; i <= workers; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    release.Wait();
                    finished.Signal();
                }, null);
            }

            Assert.That(SpinWait.SpinUntil(static () => ThreadPool.PendingWorkItemCount > 0 && ThreadPool.ThreadCount >= ThreadPoolMaximum(), Bound), Is.True, "fixture: the pool did not fill");
            Thread.Sleep(100);
            Assert.That(ThreadPool.PendingWorkItemCount, Is.GreaterThan(0), "fixture: a pool thread is still free");
            path = new Thread(() =>
            {
                try
                {
                    codePath();
                }
                catch (Exception e)
                {
                    fault = e;
                }
            })
            { IsBackground = true };
            path.Start();
            Assert.That(path.Join(Bound), Is.True, "the code path waited for the thread pool");
        }
        finally
        {
            release.Set();
            path?.Join(Bound);
            finished.Wait(Bound);
            ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
            ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
        }

        if (fault is not null) throw fault;
    }

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

    private static int ThreadPoolMaximum()
    {
        ThreadPool.GetMaxThreads(out int workers, out _);
        return workers;
    }

    private static Validator[] Validators(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => new Validator { Pubkey = new BlsPublicKey(PubkeyCacheTests.CompressedPubkey(i)) })];
}
