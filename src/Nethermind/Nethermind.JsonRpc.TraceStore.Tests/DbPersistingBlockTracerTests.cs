// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

[Parallelizable(ParallelScope.All)]
public class DbPersistingBlockTracerTests
{
    [Test]
    public void saves_traces_to_db()
    {
        ParityLikeBlockTracer parityTracer = new(ParityTraceTypes.Trace);
        MemDb memDb = new();
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
            new(parityTracer, memDb, serializer, LimboLogs.Instance);

        Transaction transaction = Build.A.Transaction.TestObject;
        Block block = Build.A.Block.WithTransactions(transaction).TestObject;
        dbPersistingTracer.StartNewBlockTrace(block);
        dbPersistingTracer.StartNewTxTrace(transaction);
        dbPersistingTracer.EndTxTrace();
        dbPersistingTracer.EndBlockTrace();

        List<ParityLikeTxTrace>? traces = serializer.Deserialize(memDb.Get(block.Hash!));
        traces.Should().BeEquivalentTo(new ParityLikeTxTrace[] { new() { BlockHash = block.Hash, TransactionPosition = 0 } });

    }
}
