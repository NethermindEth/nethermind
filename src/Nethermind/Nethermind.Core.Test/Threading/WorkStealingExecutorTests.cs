// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
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
    private const long FibNum = 32;
    private const long FibResult = 2178309;

    [Test]
    public void TestBasicFactorial()
    {
        using WorkStealingExecutor executor = new(1);

        FibanocciResult result = new FibanocciResult();
        executor.Execute(new FibanocciJob(FibNum, result));
        result.Result.Should().Be(FibResult);
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    [Parallelizable(ParallelScope.None)]
    public void TestScalability(int workerCount)
    {
        if (Environment.ProcessorCount < workerCount)
        {
            Assert.Ignore("Insufficient processor count");
        }
        FibanocciResult result = new FibanocciResult();

        TimeSpan baselineTime = TimeSpan.Zero;
        TimeSpan baselineTimeAsleep = TimeSpan.Zero;
        {
            using WorkStealingExecutor singleExecutor = new(1);

            Stopwatch sw = Stopwatch.StartNew();
            singleExecutor.Execute(new FibanocciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            result.Result = 0;
            baselineTime = sw.Elapsed;
            baselineTimeAsleep = TimeSpan.FromSeconds(singleExecutor.CalculateTotalTimeAsleep() / (double)Stopwatch.Frequency);
        }

        TimeSpan multithreadTime = TimeSpan.Zero;
        TimeSpan multithreadTimeAsleep = TimeSpan.Zero;
        {
            using WorkStealingExecutor executor = new(workerCount);

            Stopwatch sw = Stopwatch.StartNew();
            executor.Execute(new FibanocciJob(FibNum, result));
            result.Result.Should().Be(FibResult);
            multithreadTime = sw.Elapsed;
            multithreadTimeAsleep = TimeSpan.FromSeconds(executor.CalculateTotalTimeAsleep() / (double)Stopwatch.Frequency);
        }

        TestContext.Error.WriteLine($"Time {baselineTime} vs {multithreadTime}");
        TestContext.Error.WriteLine($"Time asleep {baselineTimeAsleep} vs {multithreadTimeAsleep}");

        double speedup = baselineTime / multithreadTime;
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
}
