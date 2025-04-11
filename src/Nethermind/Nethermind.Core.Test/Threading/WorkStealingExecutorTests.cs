// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class WorkStealingExecutorTests
{
    // private const long FibNum = 32;
    // private const long FibResult = 2178309;

    // Some other parameter for benchmarking
    private const long FibNum = 34;
    private const long FibResult = 5702887;
    // private const long FibNum = 38;
    // private const long FibResult = 39088169;
    // private const long FibNum = 43;
    // private const long FibResult = 433494437;

    // [TestCase(1)]
    // [TestCase(2)]
    [TestCase(4)]
    public void TestBasicFactorial(int workerCount)
    {
        using WorkStealingExecutor<FibonacciJob> executor = new(workerCount, (int)FibNum);

        FibonacciResult result = new FibonacciResult();
        Thread.Sleep(10);
        executor.Execute(new FibonacciJob(FibNum, result));
        result.Result.Should().Be(FibResult);
        executor.PrintDebug();
    }

    [Test]
    public void TestSingleThreadOverhead()
    {
        TimeSpan baselineTime = TimeSpan.Zero;
        {
            Stopwatch sw = Stopwatch.StartNew();
            long ans = RecursiveFib(FibNum);
            ans.Should().Be(FibResult);
            baselineTime = sw.Elapsed;
        }

        TimeSpan executorTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor<FibonacciJob> executor = new(1, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            FibonacciResult result = new FibonacciResult();
            executor.Execute(new FibonacciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            executorTime = sw.Elapsed;
        }

        // should be no more than 30% slower.
        TestContext.Error.WriteLine($"Time {baselineTime} vs {executorTime}");
        executorTime.Should().BeLessThan(baselineTime * 1.3);
    }

    [Test]
    [Explicit]
    [Parallelizable(ParallelScope.None)]
    public async Task TestCompareWithTasks()
    {
        FibonacciResult result = new FibonacciResult();

        TimeSpan baselineTime = TimeSpan.Zero;
        {
            Stopwatch sw = Stopwatch.StartNew();
            long ans = await TaskFib(FibNum);
            ans.Should().Be(FibResult);
            baselineTime = sw.Elapsed;
        }

        TimeSpan multithreadTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor<FibonacciJob> executor = new(Environment.ProcessorCount, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            executor.Execute(new FibonacciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            multithreadTime = sw.Elapsed;
        }

        TestContext.Error.WriteLine($"Time {baselineTime} vs {multithreadTime}");
        multithreadTime.Should().BeLessThan(baselineTime);
    }

    [TestCase(2, 1.8)]
    [TestCase(4, 3.5)]
    [TestCase(8, 6.0)]
    [TestCase(16, 8.0)]
    [TestCase(32, 10.0)]
    [Parallelizable(ParallelScope.None)]
    public void TestScalability(int workerCount, double minimumSpeedup)
    {
        if (Environment.ProcessorCount < workerCount) Assert.Ignore("Insufficient processor count");
        FibonacciResult result = new FibonacciResult();

        int baselineWorkerCount = 1; // mainly so that large fib number is easier to compare for profiling.

        TimeSpan baselineTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor<FibonacciJob> singleExecutor = new(baselineWorkerCount, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            singleExecutor.Execute(new FibonacciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            result.Result = 0;
            baselineTime = sw.Elapsed;
        }

        TimeSpan multithreadTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor<FibonacciJob> executor = new(workerCount, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            executor.Execute(new FibonacciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            executor.PrintDebug();
            multithreadTime = sw.Elapsed;
        }

        TestContext.Error.WriteLine($"Time {baselineTime} vs {multithreadTime}");
        double speedup =  (baselineTime * baselineWorkerCount) / multithreadTime;
        TestContext.Error.WriteLine($"Speedup is {speedup}");
        speedup.Should().BeGreaterThan(minimumSpeedup);
    }

    internal class FibonacciResult
    {
        internal long Result = 0;
    }

    internal struct FibonacciJob(long currentValue, FibonacciResult result): IJob<FibonacciJob>
    {
        public void Execute(Context<FibonacciJob> ctx)
        {
            if (currentValue == 0)
            {
                result.Result = 0;
                return;
            }

            if (currentValue == 1)
            {
                result.Result = 1;
                return;
            }

            FibonacciResult result1 = new FibonacciResult();
            FibonacciResult result2 = new FibonacciResult();
            var latch = ctx.PushJob(new FibonacciJob(currentValue - 2, result2));
            new FibonacciJob(currentValue - 1, result1).Execute(ctx);
            ctx.WaitForJobOrKeepBusy(latch);

            long resultNum = result1.Result + result2.Result;
            Keccak.Compute(resultNum.ToBigEndianByteArray());

            result.Result = resultNum;
        }
    }

    async Task<long> TaskFib(long currentValue)
    {
        if (currentValue == 0)
        {
            return 0;
        }

        if (currentValue == 1)
        {
            return 1;
        }

        Task<long> t2 = Task.Run(() => TaskFib(currentValue - 2));
        Task<long> t1 = TaskFib(currentValue - 1);

        long resultNum = await t2 + await t1;
        Keccak.Compute(resultNum.ToBigEndianByteArray());
        return resultNum;
    }

    static long RecursiveFib(long currentValue)
    {
        if (currentValue == 0)
        {
            return 0;
        }

        if (currentValue == 1)
        {
            return 1;
        }

        long resultNum = RecursiveFib(currentValue - 1) + RecursiveFib(currentValue - 2);
        Keccak.Compute(resultNum.ToBigEndianByteArray());
        return resultNum;
    }
}
