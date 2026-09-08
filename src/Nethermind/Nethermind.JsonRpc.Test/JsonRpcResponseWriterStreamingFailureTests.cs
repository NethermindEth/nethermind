// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// A streamed result that fails while it is being written must never hand the client a well-formed-looking
/// HTTP 200 carrying malformed JSON (#13153). While nothing has reached the transport the buffered head is
/// dropped and a framed JSON-RPC error takes its place. Once bytes are on the wire the value cannot be repaired
/// here - the emitters leave an unknown mix of open objects and arrays and <c>SkipValidation</c> means a wrong
/// closing token is written silently - so the exception propagates and the response body is aborted, exactly as
/// on 2.0.0-rc.
/// </summary>
[TestFixture]
public class JsonRpcResponseWriterStreamingFailureTests
{
    private const string MissingNodeMessage = "Node missing";

    /// <summary>
    /// Mirrors <c>ParityTxTraceStreamingResult.EmitContent</c>: open the array, run, close it in a finally. Note
    /// that the finally only balances the <em>outermost</em> array; anything the callback leaves open inside it is
    /// still torn, which is why <see cref="ReplayShapedStreamable"/> exists below.
    /// </summary>
    private sealed class ArrayStreamable(Action<Utf8JsonWriter, PipeWriter?, CancellationToken> emit)
        : JsonStreamingResultBase(new CancellationTokenSource(), LimboLogs.Instance.GetClassLogger<ArrayStreamable>())
    {
        protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
        {
            writer.WriteStartArray();
            try
            {
                emit(writer, pipeWriter, cancellationToken);
            }
            finally
            {
                writer.WriteEndArray();
            }
        }
    }

    private static MissingTrieNodeException MissingNode() => new(MissingNodeMessage, null, TreePath.Empty, TestItem.KeccakA);

    private static void FlushToTransport(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        writer.Flush();
        pipeWriter!.FlushAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private static async Task<(string Body, Exception? Thrown)> WriteAsync(IStreamableResult streamable, JsonRpcId id, CancellationToken cancellationToken = default)
    {
        Pipe pipe = new();
        using JsonRpcSuccessResponse response = new() { Id = id, Result = streamable };

        Exception? thrown = null;
        try
        {
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), cancellationToken);
        }
        catch (Exception e)
        {
            thrown = e;
        }

        await pipe.Writer.CompleteAsync();
        System.IO.Pipelines.ReadResult read = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();
        return (body, thrown);
    }

    private static void AssertWellFormed(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.That(document.RootElement.TryGetProperty("id", out _), Is.True, "envelope must carry the request id");
    }

    [TestCase(42L, "42")]
    [TestCase("abc", "\"abc\"")]
    public async Task Missing_trie_node_before_any_byte_reaches_the_transport_is_a_framed_error(object rawId, string serializedId)
    {
        JsonRpcId id = rawId is long longId ? new JsonRpcId(longId) : new JsonRpcId((string)rawId);
        ArrayStreamable streamable = new((_, _, _) => throw MissingNode());

        (string body, Exception? thrown) = await WriteAsync(streamable, id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null, "the failure is reported to the client, not to the transport");
            Assert.That(body, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.ResourceNotFound},\"message\":\"{MissingNodeMessage}\"}},\"id\":{serializedId}}}"));
        }
        AssertWellFormed(body);
    }

    [Test]
    public async Task Other_failure_before_any_byte_reaches_the_transport_is_a_framed_internal_error()
    {
        ArrayStreamable streamable = new((_, _, _) => throw new InvalidOperationException("boom"));

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(body, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InternalError},\"message\":\"Internal error\"}},\"id\":42}}"));
        }
        AssertWellFormed(body);
    }

    // Pins the InnerException walk in FindMissingTrieNode: without it this maps to -32603 "Internal error" and the
    // caller loses the reason. The wrapped shape is what production actually produces - JsonRpcService.cs:563 maps
    // `{ InnerException: MissingTrieNodeException }` for exactly this reason, and a tracer failure reaches the
    // streaming path wrapped in whatever the emitter threw. A mutant that replaces the loop with
    // `failure as MissingTrieNodeException` survives every other test in this file.
    [Test]
    public async Task Wrapped_missing_trie_node_before_the_first_flush_still_maps_to_resource_not_found()
    {
        ArrayStreamable streamable = new((_, _, _) =>
            throw new InvalidOperationException("tracer failed", MissingNode()));

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(7L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(body, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.ResourceNotFound},\"message\":\"{MissingNodeMessage}\"}},\"id\":7}}"));
        }
        AssertWellFormed(body);
    }

    // Two levels deep, so the fix cannot be a single `.InnerException` dereference either.
    [Test]
    public async Task Doubly_wrapped_missing_trie_node_still_maps_to_resource_not_found()
    {
        ArrayStreamable streamable = new((_, _, _) =>
            throw new AggregateException("outer", new InvalidOperationException("tracer failed", MissingNode())));

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(8L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(body, Does.Contain($"\"code\":{ErrorCodes.ResourceNotFound}"));
        }
        AssertWellFormed(body);
    }

    // Once bytes are on the wire the only honest outcome is to abort the body. An earlier revision of this fix
    // appended a `_streamStatus":"failed"` tail instead, which reads as a repair but is not one: it is only valid
    // when the emitter happens to have left the value balanced, and on the real trace_replay* path it does not -
    // see ReplayShapedStreamable below. A 200 carrying malformed JSON is worse than an aborted transfer, because
    // the client cannot tell it apart from a complete response until the parse fails.
    [Test]
    public async Task Failure_after_bytes_reached_the_transport_propagates_and_aborts_the_body()
    {
        ArrayStreamable streamable = new((writer, pipeWriter, cancellationToken) =>
        {
            writer.WriteNumberValue(1);
            FlushToTransport(writer, pipeWriter, cancellationToken);
            throw MissingNode();
        });

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.TypeOf<MissingTrieNodeException>());
            Assert.That(body, Does.Not.Contain("_streamStatus"), "a torn value must not be dressed up as a flagged success");
            Assert.That(body, Does.Not.Contain("\"id\":42"), "the envelope must not be closed over a torn value");
        }
    }

    /// <summary>
    /// The shape of the real <c>trace_replay*</c> path: <c>StreamingParityLikeBlockTracer.OnStart</c> writes
    /// <c>{</c> and the <c>vmTrace</c> property name and relies on the tail to close them, and neither that tracer
    /// nor <c>StreamingParityLikeTxTracer</c> has a single <c>finally</c> that unwinds on the way out.
    /// </summary>
    private sealed class ReplayShapedStreamable(Action<Utf8JsonWriter, PipeWriter?, CancellationToken> emit)
        : JsonStreamingResultBase(new CancellationTokenSource(), LimboLogs.Instance.GetClassLogger<ReplayShapedStreamable>())
    {
        protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("vmTrace"u8);
            emit(writer, pipeWriter, cancellationToken);
            writer.WriteNullValue();
            writer.WriteEndObject();
        }
    }

    // The regression the earlier revision of this fix missed: its own test double balanced the value in a finally,
    // so appending the tail happened to produce valid JSON. The real replay emitter does not, and the tail then
    // wrote a property name straight into an unclosed object - `{"vmTrace":"_streamStatus":"failed"` - which
    // SkipValidation emits without complaint.
    [Test]
    public async Task Replay_shaped_failure_after_bytes_reached_the_transport_never_emits_a_torn_body()
    {
        ReplayShapedStreamable streamable = new((writer, pipeWriter, cancellationToken) =>
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(1);
            FlushToTransport(writer, pipeWriter, cancellationToken);
            throw MissingNode();
        });

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.TypeOf<MissingTrieNodeException>());
            Assert.That(body, Does.Not.Contain("_streamStatus"));
        }
    }

    /// <summary>trace_replay* streams through a separate result type, which must be framed the same way.</summary>
    [Test]
    public async Task Replay_result_missing_trie_node_before_any_byte_reaches_the_transport_is_a_framed_error()
    {
        using ParityTxTraceFromReplayStreamingResult streamable = new(
            (_, _, _) => throw MissingNode(),
            new CancellationTokenSource(),
            LimboLogs.Instance.GetClassLogger<ParityTxTraceFromReplayStreamingResult>());

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(body, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.ResourceNotFound},\"message\":\"{MissingNodeMessage}\"}},\"id\":42}}"));
        }
        AssertWellFormed(body);
    }

    [Test]
    public async Task Successful_stream_is_byte_identical_to_before()
    {
        ArrayStreamable streamable = new((writer, pipeWriter, cancellationToken) =>
        {
            writer.WriteNumberValue(1);
            FlushToTransport(writer, pipeWriter, cancellationToken);
            writer.WriteNumberValue(2);
        });

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(body, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[1,2],\"id\":42}"));
        }
    }

    // Before this fix a cancelled or timed-out stream fell through to the success tail, so a trace that was cut
    // short by the ordinary server-side RPC timeout - the dominant truncation cause in production - was returned
    // as an empty but perfectly complete-looking result. Nothing had reached the transport, so the buffered head
    // can be dropped and the caller told what actually happened.
    [Test]
    public async Task Cancellation_before_the_first_flush_is_a_framed_error_not_an_empty_success()
    {
        ArrayStreamable streamable = new((_, _, cancellationToken) => cancellationToken.ThrowIfCancellationRequested());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L), cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.Null);
            Assert.That(body, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InternalError},\"message\":\"Request cancelled\"}},\"id\":42}}"));
        }
        AssertWellFormed(body);
    }

    [Test]
    public async Task Cancellation_after_bytes_reached_the_transport_propagates_and_aborts_the_body()
    {
        using CancellationTokenSource cts = new();
        ArrayStreamable streamable = new((writer, pipeWriter, cancellationToken) =>
        {
            writer.WriteNumberValue(1);
            FlushToTransport(writer, pipeWriter, CancellationToken.None);
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        });

        (string body, Exception? thrown) = await WriteAsync(streamable, new JsonRpcId(42L), cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.InstanceOf<OperationCanceledException>());
            Assert.That(body, Does.Not.Contain("\"id\":42"), "a cancelled stream must not be closed as if it were complete");
        }
    }
}
