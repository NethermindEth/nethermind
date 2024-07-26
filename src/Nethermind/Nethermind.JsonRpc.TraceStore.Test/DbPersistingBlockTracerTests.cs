// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

[Parallelizable(ParallelScope.All)]
public class DbPersistingBlockTracerTests
{
    private class Test
    {
        public DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> DbPersistingTracer { get; }
        public ParityLikeTraceSerializer Serializer { get; }
        public MemDb Db { get; }

        public Test()
        {
            ParityLikeBlockTracer parityTracer = new(ParityTraceTypes.Trace);
            Db = new();
            Serializer = new(LimboLogs.Instance);
            DbPersistingTracer = new(parityTracer, Db, Serializer, LimboLogs.Instance);
        }

        public (Hash256 hash, List<ParityLikeTxTrace> traces) Trace(Action<ITxTracer>? customTrace = null)
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(transaction).TestObject;
            DbPersistingTracer.StartNewBlockTrace(block);
            ITxTracer txTracer = DbPersistingTracer.StartNewTxTrace(transaction);
            customTrace?.Invoke(txTracer);
            DbPersistingTracer.EndTxTrace();
            DbPersistingTracer.EndBlockTrace();
            Hash256 hash = block.Hash!;
            return (hash, Serializer.Deserialize(Db.Get(hash))!);
        }
    }

    [Test]
    public void saves_traces_to_db()
    {
        Test test = new();
        (Hash256 hash, List<ParityLikeTxTrace> traces) = test.Trace(tracer =>
            {
                tracer.ReportAction(100, 50, TestItem.AddressA, TestItem.AddressB, TestItem.RandomDataA, ExecutionType.CALL);
                tracer.ReportAction(80, 20, TestItem.AddressB, TestItem.AddressC, TestItem.RandomDataC, ExecutionType.CREATE);
                tracer.ReportActionEnd(60, TestItem.RandomDataD);
                tracer.ReportActionEnd(50, TestItem.RandomDataB);
            }
        );

        traces.Should().BeEquivalentTo(new ParityLikeTxTrace[]
        {
            new()
            {
                BlockHash = hash,
                TransactionPosition = 0,
                Action = new ParityTraceAction
                {
                    CallType = "call",
                    From = TestItem.AddressA,
                    Gas = 100,
                    IncludeInTrace = true,
                    Input = TestItem.RandomDataA,
                    Result = new ParityTraceResult { GasUsed = 50, Output = TestItem.RandomDataB },
                    To = TestItem.AddressB,
                    TraceAddress = Array.Empty<int>(),
                    Type = "call",
                    Value = 50,
                    Subtraces =
                    [
                        new()
                        {
                            CallType = "create",
                            From = TestItem.AddressB,
                            Gas = 80,
                            IncludeInTrace = true,
                            Input = TestItem.RandomDataC,
                            Result = new ParityTraceResult { GasUsed = 20, Output = TestItem.RandomDataD },
                            Subtraces = new List<ParityTraceAction>(),
                            To = TestItem.AddressC,
                            TraceAddress = [0],
                            CreationMethod = "create",
                            Type = "create",
                            Value = 20
                        }
                    ]
                }
            }
        });
    }

    [TestCase(510)]
    [TestCase(1020)]
    [TestCase(1500)]
    public void check_depth(int depth)
    {
        // depth = depth / 2 - 1;
        Test test = new();
        (_, List<ParityLikeTxTrace> traces) = test.Trace(tracer =>
            {
                for (int i = 0; i < depth; i++)
                {
                    tracer.ReportAction(100, 50, TestItem.AddressA, TestItem.AddressB, TestItem.RandomDataA, ExecutionType.CALL);
                }

                for (int i = 0; i < depth; i++)
                {
                    tracer.ReportActionEnd(60, TestItem.RandomDataD);
                }
            }
        );

        ParityTraceAction? action = traces.FirstOrDefault()?.Action;
        int checkedDepth = 0;
        while (action is not null)
        {
            checkedDepth += 1;
            action = action.Subtraces.FirstOrDefault();
        }

        checkedDepth.Should().Be(depth);
    }
}
