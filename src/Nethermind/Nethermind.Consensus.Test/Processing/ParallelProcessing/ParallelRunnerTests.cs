// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

[Parallelizable(ParallelScope.All)]
public class ParallelRunnerTests
{
    public static IEnumerable<TestCaseData> GetTestCases()
    {
        yield return new TestCaseData((ushort) 1, (Operation[][]) [[]], RS())
        {
            TestName = "Single Empty Transaction"
        };

        yield return new TestCaseData((ushort)1, O([W(0, 1), R(0, 2), WL(1)]), RS((0, 1), (1, 1)))
        {
            TestName = "Single Transaction"
        };

        yield return new TestCaseData((ushort)2,
            O(
            [W(0, 1), R(0, 0), WL(1, 1)],
                [R(1, 5)]),
            RS((0, 1), (1, 2)))
        {
            TestName = "Two Transactions"
        };

        yield return new TestCaseData((ushort)2,
            O(
                [D(), W(0, 1), R(0, 0), WL(1, 1)],
                [R(1, 5), WL(2, 2)]),
            RS((0, 1), (1, 2), (2, 4)))
        {
            TestName = "Two Transactions Dependent, 1st delayed"
        };

        yield return GenerateNTransactions(27);
        yield return GenerateNTransactions(40);
        yield return GenerateNTransactions(50);
        yield return GenerateNTransactions(60);
        yield return GenerateNTransactions(80);
        yield return GenerateNTransactions(90);
        yield return GenerateNTransactions(100);
        yield return GenerateNTransactions(120);

        TestCaseData GenerateNTransactions(ushort n)
        {
            Dictionary<int, byte[]> results = new();
            List<Operation> currentTxOperations = new();
            List<IEnumerable<Operation>> operations = [currentTxOperations];

            currentTxOperations.Add(D(n));
            currentTxOperations.Add(W(0, 1));
            results.Add(0, [1]);

            for (int i = 1; i < n; i++)
            {
                currentTxOperations = new();
                operations.Add(currentTxOperations);
                currentTxOperations.Add(D(n - i));
                currentTxOperations.Add(R(i - 1, 255));
                currentTxOperations.Add(WL(i, 1));
                results.Add(i, [(byte)(i + 1)]);
            }

            return new TestCaseData(n, operations.ToArray(), results) { TestName = $"{n} Transactions dependent on last" };
        }
    }

    private static Dictionary<int, byte[]> RS(params IEnumerable<(int, byte)> values) =>
        values.ToDictionary<(int, byte), int, byte[]>(v => v.Item1, v => [v.Item2]);

    private static IEnumerable<Operation>[] O(params IEnumerable<IEnumerable<Operation>> operations) => operations.ToArray();

    private static Operation W(int location, byte value) =>
        new(OperationType.Write, location, [value]);

    private static Operation WL(int location, byte diff = 0) =>
        new(OperationType.Write, location, [Operation.LastRead, diff]);

    private static Operation R(int location, byte value) =>
        new (OperationType.Read, location, [value]);

    private static Operation D(int milliseconds = 100) =>
        new (OperationType.Delay, milliseconds);

    [TestCaseSource(nameof(GetTestCases))]
    public async Task Run(ushort blockSize, IEnumerable<Operation>[] operationsPerTx, Dictionary<int, byte[]> expected)
    {
        ParallelTrace<IsTracing> parallelTrace = new ParallelTrace<IsTracing>();
        MultiVersionMemory<int, IsTracing> multiVersionMemory = new MultiVersionMemory<int, IsTracing>(blockSize, parallelTrace);
        ParallelRunner<int, IsTracing> runner = new ParallelRunner<int, IsTracing>(new ParallelScheduler<IsTracing>(blockSize, parallelTrace, new DefaultObjectPool<HashSet<ushort>>(new DefaultPooledObjectPolicy<HashSet<ushort>>(), 100)), multiVersionMemory, parallelTrace, new VmMock<IsTracing>(multiVersionMemory, operationsPerTx));
        await runner.Run();
        foreach ((long, DateTime, string) trace in parallelTrace.GetTraces() ?? [])
        {
            TestContext.Out.Write(trace.Item1);
            await TestContext.Out.WriteAsync(" : ");
            await TestContext.Out.WriteAsync(trace.Item2.ToString("hh:mm:ss::fffffff"));
            await TestContext.Out.WriteAsync(" : ");
            await TestContext.Out.WriteLineAsync(trace.Item3);
        }

        Dictionary<int,byte[]> result = multiVersionMemory.Snapshot();
        await TestContext.Out.WriteLineAsync($"Expected: {{{string.Join(", ", expected.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value.ToHexString()}"))}}}");
        await TestContext.Out.WriteLineAsync($"Result  : {{{string.Join(", ", result.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value.ToHexString()}"))}}}");

        result.Should().BeEquivalentTo(expected);
    }

    public class VmMock<TLogger>(MultiVersionMemory<int, TLogger> memory, IEnumerable<Operation>[] operationsPerTx) : IVm<int> where TLogger : struct, IIsTracing
    {
        public Status TryExecute(ushort txIndex, out Version? blockingTx, out HashSet<Read<int>> readSet, out Dictionary<int, byte[]> writeSet)
        {
            readSet = new HashSet<Read<int>>();
            writeSet = new Dictionary<int, byte[]>();
            IEnumerable<Operation> operations = operationsPerTx[txIndex];
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
