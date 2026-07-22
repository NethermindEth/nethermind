// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Pbt;
using NUnit.Framework;
using Executor = Nethermind.Pbt.WorkStealingExecutor<
    Nethermind.State.Pbt.Test.WorkStealingExecutorTests.ThreadState,
    Nethermind.State.Pbt.Test.WorkStealingExecutorTests.Job>;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// The executor's contract, over a payload that is only a number: every job handed to it runs exactly
/// once, whichever thread gets to it, and its outputs are back by the time the wait returns.
/// </summary>
public class WorkStealingExecutorTests
{
    private const int Threads = 8;

    /// <summary>
    /// One thread's own state: it runs its own jobs, builds the state of every other thread, and holds
    /// the queue it hands them to — plus the tally they all share, so a job run twice or never is
    /// visible, and a count only this thread writes.
    /// </summary>
    internal sealed class ThreadState(Executor.JobQueue? queue, int[] runs)
        : IJobRunner<Job>, IJobStateProvider<ThreadState, Job>
    {
        /// <summary>Where this thread hands its jobs; the calling thread's is only known once its executor exists.</summary>
        public Executor.JobQueue Queue { get; set; } = queue!;

        /// <summary>Shared with every other thread: what they have in common, they hold in common.</summary>
        public int[] Runs => runs;

        public int Ran { get; set; }

        public ThreadState Create(Executor.JobQueue other) => new(other, runs);

        public void Execute(ref Job job) => Run(this, ref job);
    }

    internal struct Job
    {
        /// <summary>Where this job records that it ran.</summary>
        public int Index { get; init; }

        public int Input { get; init; }

        /// <summary>Children to queue and wait on before finishing, for the nested case.</summary>
        public int Children { get; init; }

        public long Output { get; set; }

        public bool Throw { get; init; }
    }

    private static void Run(ThreadState thread, ref Job job)
    {
        Interlocked.Increment(ref thread.Runs[job.Index]);
        thread.Ran++;

        if (job.Throw) throw new InvalidOperationException($"job {job.Index}");

        if (job.Children != 0)
        {
            long fromChildren = 0;
            Executor.Handle queued = default;
            for (int child = 1; child <= job.Children; child++)
            {
                Job childJob = new() { Index = job.Index + child, Input = job.Input + child };
                if (!thread.Queue.TryQueue(in childJob, ref queued)) RunHere(thread, in childJob, ref fromChildren);
            }

            if (!queued.IsEmpty)
            {
                thread.Queue.Wait(in queued);
                foreach (Executor.Node node in queued) fromChildren += node.Job.Output;
            }

            job.Output = fromChildren;
            return;
        }

        job.Output = job.Input * 2L;
    }

    /// <summary>Runs a job the queue refused, as a caller must be free to do.</summary>
    private static void RunHere(ThreadState thread, in Job job, ref long total)
    {
        Job copy = job;
        Run(thread, ref copy);
        total += copy.Output;
    }

    /// <remarks>
    /// The calling thread's state exists first and builds the rest, exactly as the trie's updater does:
    /// it is both the executor's main state and the provider of the others.
    /// </remarks>
    private static Executor Create(int[] runs, int threads)
    {
        ThreadState main = new(queue: null, runs);
        Executor executor = new(threads, main, main);
        main.Queue = executor.MainQueue;
        return executor;
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(Threads)]
    public void EveryJobRunsExactlyOnce_AndItsOutputIsBackWhenTheJoinReturns(int threads)
    {
        const int jobs = 500;
        const int rounds = 20;

        for (int round = 0; round < rounds; round++)
        {
            int[] runs = new int[jobs];
            Executor executor = Create(runs, threads);
            Executor.JobQueue main = executor.MainQueue;
            executor.Start();
            try
            {
                // a single-lane executor has no queue to offer them to, so it folds every job itself
                if (!main.CanQueue)
                {
                    Assert.That(executor.IsParallel, Is.False);
                    continue;
                }

                Executor.Handle queued = default;
                long folded = 0;
                for (int index = 0; index < jobs; index++)
                {
                    Job job = new() { Index = index, Input = index };
                    if (!main.TryQueue(in job, ref queued)) RunHere(executor.States[0], in job, ref folded);
                }

                main.Wait(in queued);

                foreach (Executor.Node node in queued)
                {
                    Assert.That(node.Error, Is.Null);
                    Assert.That(node.Job.Output, Is.EqualTo(node.Job.Input * 2L));
                }
            }
            finally
            {
                executor.Complete();
            }

            for (int index = 0; index < jobs; index++)
            {
                Assert.That(runs[index], Is.EqualTo(1), $"job {index} ran {runs[index]} times");
            }
        }
    }

    /// <summary>A full queue must refuse the job rather than drop it, leaving the caller to run it.</summary>
    [Test]
    public void AFullQueue_RefusesTheJob()
    {
        const int jobs = 4096;

        int[] runs = new int[jobs];
        Executor executor = Create(runs, Threads);
        Executor.JobQueue main = executor.MainQueue;

        // deliberately not started: with no thief draining it, the queue fills and stays full
        int refused = 0;
        Executor.Handle queued = default;
        try
        {
            for (int index = 0; index < jobs; index++)
            {
                Job job = new() { Index = index, Input = index };
                if (!main.TryQueue(in job, ref queued)) refused++;
            }

            Assert.That(refused, Is.GreaterThan(0), "the queue must fill");
            main.Wait(in queued);
        }
        finally
        {
            executor.Complete();
        }

        int ran = 0;
        for (int index = 0; index < jobs; index++) ran += runs[index];
        Assert.That(ran, Is.EqualTo(jobs - refused), "every job the queue took must have run, and only those");
    }

    /// <summary>A job that throws leaves its exception on its own node and its siblings alone.</summary>
    [Test]
    public void AJobThatThrows_LandsOnItsOwnNode()
    {
        const int jobs = 64;

        int[] runs = new int[jobs];
        Executor executor = Create(runs, Threads);
        Executor.JobQueue main = executor.MainQueue;
        executor.Start();
        try
        {
            Executor.Handle queued = default;
            for (int index = 0; index < jobs; index++)
            {
                Job job = new() { Index = index, Input = index, Throw = index % 8 == 0 };
                main.TryQueue(in job, ref queued);
            }

            main.Wait(in queued);

            foreach (Executor.Node node in queued)
            {
                if (node.Job.Throw) Assert.That(node.Error, Is.InstanceOf<InvalidOperationException>());
                else Assert.That(node.Error, Is.Null);
            }
        }
        finally
        {
            executor.Complete();
        }
    }

    /// <summary>A job may queue and wait on jobs of its own, which is what a recursive descent does.</summary>
    [Test]
    public void AJobMayQueueAndWaitOnJobsOfItsOwn()
    {
        const int children = 8;
        const int parents = 32;
        const int stride = children + 1;

        int[] runs = new int[parents * stride];
        Executor executor = Create(runs, Threads);
        Executor.JobQueue main = executor.MainQueue;
        executor.Start();
        try
        {
            Executor.Handle queued = default;
            for (int parent = 0; parent < parents; parent++)
            {
                Job job = new() { Index = parent * stride, Input = parent, Children = children };
                main.TryQueue(in job, ref queued);
            }

            main.Wait(in queued);

            foreach (Executor.Node node in queued)
            {
                Assert.That(node.Error, Is.Null);

                // every child folded input + child, doubled
                long expected = 0;
                for (int child = 1; child <= children; child++) expected += (node.Job.Input + child) * 2L;
                Assert.That(node.Job.Output, Is.EqualTo(expected));
            }
        }
        finally
        {
            executor.Complete();
        }

        foreach (int ran in runs) Assert.That(ran, Is.LessThanOrEqualTo(1), "a job ran twice");
    }
}
