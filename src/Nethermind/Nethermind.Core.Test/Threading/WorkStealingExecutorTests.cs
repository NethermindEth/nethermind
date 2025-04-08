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
    // private const long FibNum = 34;
    // private const long FibResult = 5702887;

    private const long FibNum = 40;
    private const long FibResult = 102334155;

    // [TestCase(1)]
    // [TestCase(2)]
    // [TestCase(16)]
    [TestCase(32)]
    public void TestBasicFactorial(int workerCount)
    {
        using WorkStealingExecutor executor = new(workerCount, (int)FibNum);

        FibanocciResult result = new FibanocciResult();
        executor.Execute(new FibanocciJob(FibNum, result));
        result.Result.Should().Be(FibResult);
    }

    [Test]
    [Explicit]
    [Parallelizable(ParallelScope.None)]
    public async Task TestCompareWithTasks()
    {
        FibanocciResult result = new FibanocciResult();

        TimeSpan baselineTime = TimeSpan.Zero;
        {
            Stopwatch sw = Stopwatch.StartNew();
            long ans = await TaskFib(FibNum);
            ans.Should().Be(FibResult);
            baselineTime = sw.Elapsed;
        }

        TimeSpan multithreadTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor executor = new(Environment.ProcessorCount, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            executor.Execute(new FibanocciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            multithreadTime = sw.Elapsed;
            var multithreadTimeAsleep = TimeSpan.FromSeconds(executor.CalculateTotalTimeAsleep() / (double)Stopwatch.Frequency);
            var timeStealing = TimeSpan.FromSeconds(executor.CalculateTotalTimeStealing() / (double)Stopwatch.Frequency);
            var timeNotifying = TimeSpan.FromSeconds(executor.CalculateTotalTimeNotifying() / (double)Stopwatch.Frequency);
            TestContext.Error.WriteLine($"Time stealing {timeStealing}");
            TestContext.Error.WriteLine($"Time notifying {timeNotifying}");
            TestContext.Error.WriteLine($"Time asleep {multithreadTimeAsleep}");
        }

        TestContext.Error.WriteLine($"Time {baselineTime} vs {multithreadTime}");
        multithreadTime.Should().BeLessThan(baselineTime);
    }

    // [TestCase(2)]
    // [TestCase(4)]
    // [TestCase(8)]
    // [TestCase(16)]
    [TestCase(32)]
    [Parallelizable(ParallelScope.None)]
    public void TestScalability(int workerCount)
    {
        if (Environment.ProcessorCount < workerCount)
        {
            Assert.Ignore("Insufficient processor count");
        }

        int baselineWorkerCount = 4; // mainly so that large fib number is easier to compare for profiling.
        FibanocciResult result = new FibanocciResult();

        TimeSpan baselineTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor singleExecutor = new(baselineWorkerCount, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            singleExecutor.Execute(new FibanocciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            result.Result = 0;
            baselineTime = sw.Elapsed;
        }

        TimeSpan multithreadTime = TimeSpan.Zero;
        {
            using WorkStealingExecutor executor = new(workerCount, (int)FibNum);

            Stopwatch sw = Stopwatch.StartNew();
            executor.Execute(new FibanocciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            multithreadTime = sw.Elapsed;
            var timeStealing = TimeSpan.FromSeconds(executor.CalculateTotalTimeStealing() / (double)Stopwatch.Frequency);
            var timeNotifying = TimeSpan.FromSeconds(executor.CalculateTotalTimeNotifying() / (double)Stopwatch.Frequency);
            var multithreadTimeAsleep = TimeSpan.FromSeconds(executor.CalculateTotalTimeAsleep() / (double)Stopwatch.Frequency);
            TestContext.Error.WriteLine($"Time stealing {timeStealing}");
            TestContext.Error.WriteLine($"Time notifying {timeNotifying}");
            TestContext.Error.WriteLine($"Time asleep {multithreadTimeAsleep}");
        }

        TestContext.Error.WriteLine($"Time {baselineTime} vs {multithreadTime}");

        double speedup = (baselineTime * baselineWorkerCount) / multithreadTime;
        speedup.Should().BeGreaterThan(workerCount * 0.9);
    }

    internal class FibanocciResult
    {
        internal long Result = 0;
    }

    internal struct FibanocciJob(long currentValue, FibanocciResult result): IJob
    {
        public void Execute(Context ctx)
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


            FibanocciResult result1 = new FibanocciResult();
            FibanocciResult result2 = new FibanocciResult();
            ctx.Fork(
                new FibanocciJob(currentValue - 1, result1),
                new FibanocciJob(currentValue - 2, result2)
            );

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
}
