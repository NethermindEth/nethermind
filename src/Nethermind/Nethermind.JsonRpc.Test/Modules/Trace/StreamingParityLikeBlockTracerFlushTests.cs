// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Trace;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

public class StreamingParityLikeBlockTracerFlushTests
{
    [Test]
    public void WriteTraces_keeps_small_traces_buffered_until_the_threshold()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        using Utf8JsonWriter writer = new(pipe.Writer, new JsonWriterOptions { SkipValidation = true });
        using StreamingParityLikeBlockTracer tracer = new(ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false, writer, pipe.Writer, CancellationToken.None);

        tracer.WriteTraces([BuildRewardTrace()]);
        tracer.WriteTraces([BuildRewardTrace()]);

        Assert.That(pipe.Reader.TryRead(out _), Is.False);

        while (writer.BytesCommitted < StreamingParityLikeBlockTracer.FlushThresholdBytes)
        {
            tracer.WriteTraces([BuildRewardTrace()]);
        }

        Assert.That(pipe.Reader.TryRead(out ReadResult read), Is.True);
        Assert.That(read.Buffer.Length, Is.GreaterThanOrEqualTo(StreamingParityLikeBlockTracer.FlushThresholdBytes));
        pipe.Reader.AdvanceTo(read.Buffer.End);
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
