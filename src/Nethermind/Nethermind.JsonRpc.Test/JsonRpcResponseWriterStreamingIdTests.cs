// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// Locks in that the streamable JSON-RPC response envelope written by
/// <see cref="JsonRpcResponseWriter.WriteAsync"/> picks up the branch's
/// raw-decimal / int64 / string / null ID handling. Regression guard for
/// future streaming-result implementations.
/// </summary>
[TestFixture]
public class JsonRpcResponseWriterStreamingIdTests
{
    private sealed class StubStreamable : IStreamableResult
    {
        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("\"ok\""u8);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingStreamable(bool emitValueBeforeThrow, Exception failure)
        : JsonStreamingResultBase(new CancellationTokenSource(), LimboLogs.Instance.GetClassLogger<ThrowingStreamable>())
    {
        protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
        {
            if (emitValueBeforeThrow)
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            throw failure;
        }
    }

    public static IEnumerable<TestCaseData> IdCases()
    {
        yield return new TestCaseData(new JsonRpcId(42L), "42").SetName("Int64Id_UnquotedNumber");
        // Value beyond Int64.MaxValue exercises the decimal branch of WriteIdRaw.
        yield return new TestCaseData(new JsonRpcId(9876543210987654321m), "9876543210987654321").SetName("DecimalId_RawPreserved");
        yield return new TestCaseData(new JsonRpcId("abc"), "\"abc\"").SetName("StringId_Quoted");
        yield return new TestCaseData(JsonRpcId.Null, "null").SetName("NullId_AsJsonNull");
        yield return new TestCaseData(JsonRpcId.Missing, "null").SetName("MissingId_AsJsonNull");
    }

    [TestCaseSource(nameof(IdCases))]
    public async Task Streaming_envelope_serializes_id_correctly(JsonRpcId id, string serializedId)
    {
        Pipe pipe = new();
        using JsonRpcSuccessResponse response = new() { Id = id, Result = new StubStreamable() };

        await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        System.IO.Pipelines.ReadResult read = await pipe.Reader.ReadAsync();
        string envelope = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();

        Assert.That(envelope, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"ok\",\"id\":{serializedId}}}"));
    }

    public static IEnumerable<TestCaseData> FailureCases()
    {
        yield return new TestCaseData(true, new InvalidOperationException("boom"), "{\"jsonrpc\":\"2.0\",\"result\":[],\"_streamStatus\":\"failed\",\"id\":42}")
            .SetName("FailureAfterValue_EnvelopeClosedAndFlagged");
        yield return new TestCaseData(false, new InvalidOperationException("boom"), "{\"jsonrpc\":\"2.0\",\"result\":null,\"_streamStatus\":\"failed\",\"id\":42}")
            .SetName("FailureBeforeValue_ResultIsNull");
        // A caller that gave up mid-stream must still see an unterminated body, so it cannot mistake a
        // partial payload for a complete one.
        yield return new TestCaseData(true, new OperationCanceledException(), "{\"jsonrpc\":\"2.0\",\"result\":[]")
            .SetName("Cancellation_LeavesEnvelopeTruncated");
    }

    [TestCaseSource(nameof(FailureCases))]
    public async Task Streaming_failure_does_not_truncate_the_envelope(bool emitValueBeforeThrow, Exception failure, string expectedEnvelope)
    {
        Pipe pipe = new();
        using JsonRpcSuccessResponse response = new() { Id = new JsonRpcId(42L), Result = new ThrowingStreamable(emitValueBeforeThrow, failure) };

        Exception? thrown = Assert.ThrowsAsync(failure.GetType(), async () =>
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None));
        await pipe.Writer.CompleteAsync();

        System.IO.Pipelines.ReadResult read = await pipe.Reader.ReadAsync();
        string envelope = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(failure), "the failure must not be swallowed");
            Assert.That(envelope, Is.EqualTo(expectedEnvelope));
        }
    }

    [Test]
    public void Fractional_decimal_id_is_rejected_at_construction()
    {
        Action act = () => new JsonRpcId(1.5m);

        Assert.That(act, Throws.TypeOf<NotSupportedException>());
    }
}
