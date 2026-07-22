// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Pbt;
using NUnit.Framework;
using Executor = Nethermind.Pbt.WorkStealingExecutor<
    Nethermind.State.Pbt.Test.WorkStealingExecutorTests.Shared,
    Nethermind.State.Pbt.Test.WorkStealingExecutorTests.LaneState,
    Nethermind.State.Pbt.Test.WorkStealingExecutorTests.Job>;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// The executor's contract, over a payload that is only a number: every job handed to it runs exactly
/// once, whichever thread gets to it, and its outputs are back by the time the join returns.
/// </summary>
public class WorkStealingExecutorTests
{
    private const int Lanes = 8;

    /// <summary>What every lane shares: a tally per job, so a job run twice or never is visible.</summary>
    internal sealed class Shared(int jobCount)
    {
        public int[] Runs { get; } = new int[jobCount];
    }

    /// <summary>One lane's own state, which is the lane itself plus what only that thread writes.</summary>
    internal sealed class LaneState(Executor.Lane lane)
    {
        public Executor.Lane Lane => lane;

        public int Ran { get; set; }
    }

    internal struct Job
    {
        /// <summary>Where this job records that it ran.</summary>
        public int Index { get; init; }

        public int Input { get; init; }

        /// <summary>Children to spawn and join before finishing, for the nested case.</summary>
        public int Children { get; init; }

        public long Output { get; set; }

        public bool Throw { get; init; }
    }

    private static void Run(Shared shared, LaneState lane, ref Job job)
    {
        Interlocked.Increment(ref shared.Runs[job.Index]);
        lane.Ran++;

        if (job.Throw) throw new InvalidOperationException($"job {job.Index}");

        if (job.Children != 0)
        {
            long fromChildren = 0;
            Executor.Node? spawned = null;
            long mark = lane.Lane.QueueMark;
            for (int child = 1; child <= job.Children; child++)
            {
                Job childJob = new() { Index = job.Index + child, Input = job.Input + child };
                Executor.Node? node = lane.Lane.TrySpawn(in childJob, spawned);
                if (node is null) RunHere(shared, lane, in childJob, ref fromChildren);
                else spawned = node;
            }

            if (spawned is not null)
            {
                lane.Lane.Join(spawned, mark);
                for (Executor.Node? node = spawned; node is not null; node = node.Next) fromChildren += node.Job.Output;
            }

            job.Output = fromChildren;
            return;
        }

        job.Output = job.Input * 2L;
    }

    /// <summary>Folds a job the queue refused, as a caller must be free to do.</summary>
    private static void RunHere(Shared shared, LaneState lane, in Job job, ref long total)
    {
        Job copy = job;
        Run(shared, lane, ref copy);
        total += copy.Output;
    }

    private static Executor Create(Shared shared, int lanes) =>
        new(lanes, shared, static (_, lane) => new LaneState(lane), Run);

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(Lanes)]
    public void EveryJobRunsExactlyOnce_AndItsOutputIsBackWhenTheJoinReturns(int lanes)
    {
        const int jobs = 500;
        const int rounds = 20;

        for (int round = 0; round < rounds; round++)
        {
            Shared shared = new(jobs);
            Executor executor = Create(shared, lanes);
            Executor.Lane main = executor.MainLane;
            executor.Start();
            try
            {
                // a single-lane executor has no queue to offer them to, so it folds every job itself
                if (!main.CanSpawn)
                {
                    Assert.That(executor.IsParallel, Is.False);
                    continue;
                }

                Executor.Node? spawned = null;
                long mark = main.QueueMark;
                long folded = 0;
                for (int index = 0; index < jobs; index++)
                {
                    Job job = new() { Index = index, Input = index };
                    Executor.Node? node = main.TrySpawn(in job, spawned);
                    if (node is null) RunHere(shared, executor.Workers[0], in job, ref folded);
                    else spawned = node;
                }

                main.Join(spawned!, mark);

                for (Executor.Node? node = spawned; node is not null; node = node.Next)
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
                Assert.That(shared.Runs[index], Is.EqualTo(1), $"job {index} ran {shared.Runs[index]} times");
            }
        }
    }

    /// <summary>A full queue must refuse the spawn rather than drop the job, leaving the caller to run it.</summary>
    [Test]
    public void AFullQueue_RefusesTheSpawn()
    {
        const int jobs = 4096;

        Shared shared = new(jobs);
        Executor executor = Create(shared, Lanes);
        Executor.Lane main = executor.MainLane;

        // deliberately not started: with no thief draining it, the queue fills and stays full
        int refused = 0;
        Executor.Node? spawned = null;
        long mark = main.QueueMark;
        try
        {
            for (int index = 0; index < jobs; index++)
            {
                Job job = new() { Index = index, Input = index };
                Executor.Node? node = main.TrySpawn(in job, spawned);
                if (node is null) refused++;
                else spawned = node;
            }

            Assert.That(refused, Is.GreaterThan(0), "the queue must fill");
            main.Join(spawned!, mark);
        }
        finally
        {
            executor.Complete();
        }

        int ran = 0;
        for (int index = 0; index < jobs; index++) ran += shared.Runs[index];
        Assert.That(ran, Is.EqualTo(jobs - refused), "every job the queue took must have run, and only those");
    }

    /// <summary>A job that throws leaves its exception on its own node and its siblings alone.</summary>
    [Test]
    public void AJobThatThrows_LandsOnItsOwnNode()
    {
        const int jobs = 64;

        Shared shared = new(jobs);
        Executor executor = Create(shared, Lanes);
        Executor.Lane main = executor.MainLane;
        executor.Start();
        try
        {
            Executor.Node? spawned = null;
            long mark = main.QueueMark;
            for (int index = 0; index < jobs; index++)
            {
                Job job = new() { Index = index, Input = index, Throw = index % 8 == 0 };
                spawned = main.TrySpawn(in job, spawned) ?? spawned;
            }

            main.Join(spawned!, mark);

            for (Executor.Node? node = spawned; node is not null; node = node.Next)
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

    /// <summary>A job may spawn and join on the lane running it, which is what a recursive descent does.</summary>
    [Test]
    public void AJobMaySpawnAndJoinJobsOfItsOwn()
    {
        const int children = 8;
        const int parents = 32;
        const int stride = children + 1;

        Shared shared = new(parents * stride);
        Executor executor = Create(shared, Lanes);
        Executor.Lane main = executor.MainLane;
        executor.Start();
        try
        {
            Executor.Node? spawned = null;
            long mark = main.QueueMark;
            for (int parent = 0; parent < parents; parent++)
            {
                Job job = new() { Index = parent * stride, Input = parent, Children = children };
                spawned = main.TrySpawn(in job, spawned) ?? spawned;
            }

            main.Join(spawned!, mark);

            for (Executor.Node? node = spawned; node is not null; node = node.Next)
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

        foreach (int runs in shared.Runs) Assert.That(runs, Is.LessThanOrEqualTo(1), "a job ran twice");
    }
}
