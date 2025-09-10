// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Consensus.Processing.ParallelProcessing;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Version = Nethermind.Consensus.Processing.ParallelProcessing.Version;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing;

[Parallelizable(ParallelScope.Self)]
public class ParallelRunnerTests
{
    public static IEnumerable<TestCaseData> GetDependantTestCases()
    {
        yield return new TestCaseData(2,
            O(
                [D(), W(0, 1), R(0, 0), WL(1, 1)],
                [R(1, 5), WL(2, 2)]),
            RS((0, 1), (1, 2), (2, 4)))
        {
            TestName = "Two Transactions Dependent, 1st delayed"
        };

        const int stressIterations = 1;
        for (int i = 0; i < stressIterations; i++)
        {
            yield return GenerateNDependantTransactions(10);
            yield return GenerateNDependantTransactions(27);
            yield return GenerateNDependantTransactions(40);
            yield return GenerateNDependantTransactions(50);
            yield return GenerateNDependantTransactions(60);
            yield return GenerateNDependantTransactions(80);
            yield return GenerateNDependantTransactions(90);
            yield return GenerateNDependantTransactions(100);
            yield return GenerateNDependantTransactions(120);
        }

        // Generates N transactions that each K transaction is dependent on K-1 transaction
        // Also K transaction will have a N-K delay before execution
        // So basically each transaction is dependent on previous and previous will take longer to complete
        TestCaseData GenerateNDependantTransactions(int n)
        {
            Dictionary<int, byte[]> results = new();
            List<Operation> currentTxOperations = new();
            List<Operation[]> operations = [];

            currentTxOperations.Add(D(n));
            currentTxOperations.Add(W(0, 1));
            operations.Add(currentTxOperations.ToArray());
            results.Add(0, [1]);

            for (int i = 1; i < n; i++)
            {
                currentTxOperations = new();
                currentTxOperations.Add(D(n - i));
                currentTxOperations.Add(R(i - 1, 255));
                currentTxOperations.Add(WL(i, 1));
                operations.Add(currentTxOperations.ToArray());
                results.Add(i, [(byte)(i + 1)]);
            }

            return new TestCaseData(n, operations.ToArray(), results) { TestName = $"Dependent Transactions {n}" };
        }
    }

    public static IEnumerable<TestCaseData> GetIndependantTestCases()
    {
        yield return new TestCaseData(1, (Operation[][])[[]], RS())
        {
            TestName = "Single Empty Transaction"
        };

        yield return new TestCaseData(1, O([W(0, 1), R(0, 2), WL(1)]), RS((0, 1), (1, 1)))
        {
            TestName = "Single Transaction"
        };

        yield return new TestCaseData(2,
            O(
                [W(0, 1), R(0, 0), WL(1, 1)],
                [R(1, 5)]),
            RS((0, 1), (1, 2)))
        {
            TestName = "Two Transactions"
        };

        yield return GenerateNIndependentTransactions(10);
        yield return GenerateNIndependentTransactions(100);
        yield return GenerateNIndependentTransactions(200);
        yield return GenerateNIndependentTransactions(1_000);
        yield return GenerateNIndependentTransactions(10_000);
        yield return GenerateNIndependentTransactions(ushort.MaxValue - 1);
        yield return GenerateNIndependentTransactions(100_000);
        yield return GenerateNIndependentTransactions(1_000_000);

        TestCaseData GenerateNIndependentTransactions(int n)
        {
            Dictionary<int, byte[]> results = new();
            List<Operation[]> operations = [];

            for (int i = 0; i < n; i++)
            {
                List<Operation> currentTxOperations = new();
                // currentTxOperations.Add(D(1));
                currentTxOperations.Add(W(i, 1));
                operations.Add(currentTxOperations.ToArray());
                results.Add(i, [1]);
            }

            return new TestCaseData(n, operations.ToArray(), results) { TestName = $"Independent Transactions {n}" };
        }
    }

    // RS -> Result
    private static Dictionary<int, byte[]> RS(params IEnumerable<(int, byte)> values) =>
        values.ToDictionary<(int, byte), int, byte[]>(v => v.Item1, v => [v.Item2]);

    private static Operation[][] O(params IEnumerable<IEnumerable<Operation>> operations) => operations.Select(o => o.ToArray()).ToArray();

    // W - Write
    private static Operation W(int location, byte value) =>
        new(OperationType.Write, location, [value]);

    // WL - Write last read value
    private static Operation WL(int location, byte diff = 0) =>
        new(OperationType.Write, location, [Operation.LastRead, diff]);

    // R - Read
    private static Operation R(int location, byte value) =>
        new(OperationType.Read, location, [value]);

    // D - Delay
    private static Operation D(int milliseconds = 100) =>
        new(OperationType.Delay, milliseconds);

    [TestCaseSource(nameof(GetDependantTestCases))]
    public Task RunDependant(int blockSize, Operation[][] operationsPerTx, Dictionary<int, byte[]> expected) =>
        Run<IsTracing>(blockSize, operationsPerTx, expected);

    [TestCaseSource(nameof(GetIndependantTestCases))]
    public Task RunIndependant(int blockSize, Operation[][] operationsPerTx, Dictionary<int, byte[]> expected) =>
        Run<NotTracing>(blockSize, operationsPerTx, expected);

    private async Task Run<T>(int blockSize, Operation[][] operationsPerTx, Dictionary<int, byte[]> expected) where T : struct, IIsTracing
    {
        ParallelTrace<T> parallelTrace = new ParallelTrace<T>();
        MultiVersionMemory<int, byte[], T> multiVersionMemory = new MultiVersionMemory<int, byte[], T>(blockSize, parallelTrace);
        ObjectPool<HashSet<int>> setObjectPool = new DefaultObjectPool<HashSet<int>>(new DefaultPooledObjectPolicy<HashSet<int>>(), 1024);
        ParallelScheduler<T> parallelScheduler = new ParallelScheduler<T>(blockSize, parallelTrace, setObjectPool);
        VmMock<T> vmMock = new VmMock<T>(blockSize, multiVersionMemory, operationsPerTx);
        ParallelRunner<int, byte[], T> runner = new ParallelRunner<int, byte[], T>(parallelScheduler, multiVersionMemory, parallelTrace, vmMock, 12);

        long start = Stopwatch.GetTimestamp();
        Task runnerTask = runner.Run();
        Task completedTask = await Task.WhenAny(runnerTask, Task.Delay(TimeSpan.FromSeconds(20)));
        Dictionary<int, byte[]> result = multiVersionMemory.Snapshot();
        if (typeof(T) == typeof(IsTracing)) await PrintInfo(parallelTrace, result, expected);
        TimeSpan time = Stopwatch.GetElapsedTime(start);
        await TestContext.Out.WriteLineAsync($"Execution time: {time.TotalMilliseconds}ms");

        if (completedTask == runnerTask)
        {
            result.Should().BeEquivalentTo(expected);
        }
        else
        {
            Assert.Fail($"Timeout! {DateTime.Now:hh:mm:ss::fffffff}");
        }
    }

    private static async Task PrintInfo<T>(ParallelTrace<T> parallelTrace, Dictionary<int, byte[]> result, Dictionary<int, byte[]> expected) where T : struct, IIsTracing
    {
        foreach ((long, DateTime, string) trace in parallelTrace.GetTraces() ?? [])
        {
            TestContext.Out.Write(trace.Item1);
            await TestContext.Out.WriteAsync(" : ");
            await TestContext.Out.WriteAsync(trace.Item2.ToString("hh:mm:ss::fffffff"));
            await TestContext.Out.WriteAsync(" : ");
            await TestContext.Out.WriteLineAsync(trace.Item3);
        }

        await TestContext.Out.WriteLineAsync($"Expected: {{{string.Join(", ", expected.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value.ToHexString()}"))}}}");
        await TestContext.Out.WriteLineAsync($"Result  : {{{string.Join(", ", result.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value.ToHexString()}"))}}}");

    }

    public class VmMock<TLogger>(int blockSize, MultiVersionMemory<int, byte[], TLogger> memory, Operation[][] operationsPerTx) : IVm<int, byte[]> where TLogger : struct, IIsTracing
    {
        private readonly HashSet<Read<int>>[] _readSets = Enumerable.Range(0, blockSize).Select(_ => new HashSet<Read<int>>()).ToArray();
        private readonly Dictionary<int, byte[]>[] _writeSets = Enumerable.Range(0, blockSize).Select(_ => new Dictionary<int, byte[]>()).ToArray();

        public Status TryExecute(int txIndex, out Version? blockingTx, out HashSet<Read<int>> readSet, out Dictionary<int, byte[]> writeSet)
        {
            readSet = _readSets[txIndex];
            readSet.Clear();
            writeSet = _writeSets[txIndex];
            writeSet.Clear();
            Operation[] operations = operationsPerTx[txIndex];
            byte[] lastRead = null;

            foreach (Operation operation in operations)
            {
                switch (operation.Type)
                {
                    case OperationType.Read:
                    {
                        if (!writeSet.TryGetValue(operation.Location, out lastRead))
                        {
                            Status result = memory.TryRead(operation.Location, txIndex, out Version version, out var value);
                            switch (result)
                            {
                                case Status.NotFound:
                                    lastRead = operation.Value; // read from storage
                                    readSet.Add(new Read<int>(operation.Location, Version.Empty));
                                    break;
                                case Status.Ok:
                                    lastRead = value; // use value from previous transaction
                                    readSet.Add(new Read<int>(operation.Location, version));
                                    break;
                                case Status.ReadError:
                                    blockingTx = version;
                                    return Status.ReadError;
                            }
                        }

                        break;
                    }
                    case OperationType.Write:
                        writeSet[operation.Location] = operation.Value[0] == Operation.LastRead ? [(byte)(operation.Value[1] + lastRead[0])] : operation.Value;
                        break;
                    case OperationType.Delay:
                        Thread.Sleep(operation.Location);
                        break;
                }
            }

            blockingTx = null;
            return Status.Ok;
        }
    }

    public enum OperationType { Read, Write, Delay }

    public readonly record struct Operation(OperationType Type, int Location, byte[] Value = null)
    {
        public static byte LastRead = byte.MaxValue;
    }

}
