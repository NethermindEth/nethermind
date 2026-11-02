// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.Trace;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

public class StreamingParityLikeBlockTracerFlushTests
{
    [Test]
    public void Write_keeps_small_traces_buffered_until_the_threshold()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        using Utf8JsonWriter writer = new(pipe.Writer, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, pipe.Writer, CancellationToken.None);

        tracer.Write([BuildRewardTrace()]);
        tracer.Write([BuildRewardTrace()]);

        Assert.That(pipe.Reader.TryRead(out _), Is.False);

        while (writer.BytesCommitted < StreamingParityLikeBlockTracer.FlushThresholdBytes)
        {
            tracer.Write([BuildRewardTrace()]);
        }

#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(pipe.Reader.TryRead(out ReadResult read), Is.True);
#pragma warning restore NUnit2045
        Assert.That(read.Buffer.Length, Is.GreaterThanOrEqualTo(StreamingParityLikeBlockTracer.FlushThresholdBytes));
        pipe.Reader.AdvanceTo(read.Buffer.End);
    }

    [Test]
    public void ParallelTxTracers_ReuseTheResultOfADisposedTransaction()
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, null, CancellationToken.None);
        using ParallelParityTxTracerPool pool = tracer.CreateParallelTxTracers(Build.A.Block.TestObject, ParityTraceTypes.Trace);

        IBlockTracer<ParityLikeTxTrace> first = pool.For(TestItem.KeccakA);
        IReadOnlyCollection<ParityLikeTxTrace> result = first.BuildResult();
        result.TryDispose();
        result.TryDispose();
        IBlockTracer<ParityLikeTxTrace> second = pool.For(TestItem.KeccakB);
        IBlockTracer<ParityLikeTxTrace> third = pool.For(TestItem.KeccakC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(first), "a disposed result traces the next transaction");
            Assert.That(third, Is.Not.SameAs(first), "a result disposed twice is pooled once");
            Assert.That(result, Is.InstanceOf<IReadOnlyList<ParityLikeTxTrace>>(), "the streams read it by index, without an enumerator");
        }
    }

    private static ParityLikeTxTrace BuildRewardTrace() => new()
    {
        BlockHash = TestItem.KeccakA,
        BlockNumber = 7,
        Action = new ParityTraceAction
        {
            RewardType = "block",
            Value = 2,
            Author = TestItem.AddressE,
            CallType = "reward",
            TraceAddress = default,
            Type = "reward",
            Result = null
        }
    };
}
