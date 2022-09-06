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
        DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
            new(parityTracer, memDb, static t => TraceSerializer.Serialize(t), LimboLogs.Instance);

        Block block = Build.A.Block.TestObject;
        dbPersistingTracer.StartNewBlockTrace(block);
        dbPersistingTracer.StartNewTxTrace(Build.A.Transaction.TestObject);
        dbPersistingTracer.EndTxTrace();
        dbPersistingTracer.EndBlockTrace();

        ParityLikeTxTrace[]? traces = TraceSerializer.Deserialize<ParityLikeTxTrace[]>(memDb.Get(block.Hash!));
        traces.Should().BeEquivalentTo(new ParityLikeTxTrace[] { new() { BlockHash = new Keccak("0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6"), TransactionPosition = -1 } });

    }
}
