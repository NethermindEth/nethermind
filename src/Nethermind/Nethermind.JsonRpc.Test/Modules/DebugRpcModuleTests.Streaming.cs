// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task GethLikeTxTraceStreamingSingleResult_WhenCancelledMidTrace_ClosesJsonEnvelope()
    {
        using CancellationTokenSource requestCts = new();
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingSingleResult result = new(
            (writer, _, ct) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("pc"u8);
                writer.WriteNumberValue(0);
                writer.WriteEndObject();

                requestCts.Cancel();
                ct.ThrowIfCancellationRequested();
                return null;
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();

        Assert.That(async () => await result.WriteToAsync(pipe.Writer, requestCts.Token), Throws.Nothing,
            "WriteToAsync swallows OperationCanceledException so the HTTP layer can close the response cleanly");

        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Action parse = () => JToken.Parse(body);
        Assert.That(parse, Throws.Nothing, "the finally block must close the structLogs array and the outer object even when cancellation fires");

        JToken parsed = JToken.Parse(body);
        Assert.That(parsed["failed"]!.Value<bool>(), Is.True, "a cancelled mid-trace is reported as failed");
    }

    [Test]
    public void GethLikeTxTraceStreamingSingleResult_WhenTraceThrows_WritesErrorAndErrorCode()
    {
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingSingleResult result = new(
            (writer, _, _) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("pc"u8);
                writer.WriteNumberValue(0);
                writer.WriteEndObject();

                throw new InvalidOperationException("simulated mid-trace failure");
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        ArrayBufferWriter<byte> bufferWriter = new();
        using Utf8JsonWriter writer = new(bufferWriter);
        result.WriteAsJson(writer);
        writer.Flush();

        JToken parsed = JToken.Parse(Encoding.UTF8.GetString(bufferWriter.WrittenSpan));
        Assert.That(parsed["failed"]!.Value<bool>(), Is.True);
        Assert.That(parsed["error"]!.Value<string>(), Does.Contain("simulated mid-trace failure"));
        Assert.That(parsed["errorCode"]!.Value<int>(), Is.EqualTo(ErrorCodes.InternalError), "generic exceptions map to InternalError; InsufficientBalanceException maps to InvalidInput");
    }

    [Test]
    public void GethLikeBlockEnvelopeStreamingTracer_WhenDisposedMidTx_EmitsTxHashAndClosesEnvelope()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;

        ArrayBufferWriter<byte> outerBuffer = new();
        using Utf8JsonWriter jsonWriter = new(outerBuffer);

        jsonWriter.WriteStartArray();

        using (GethLikeBlockEnvelopeStreamingTracer tracer = new(GethTraceOptions.Default, jsonWriter, pipeWriter: null, CancellationToken.None))
        {
            ((Evm.Tracing.IBlockTracer)tracer).StartNewTxTrace(tx);
            // simulate a mid-tx failure: no MarkAsSuccess/MarkAsFailed is called, OnEnd never runs.
        }

        jsonWriter.WriteEndArray();
        jsonWriter.Flush();

        JArray result = (JArray)JToken.Parse(Encoding.UTF8.GetString(outerBuffer.WrittenSpan));
        Assert.That(result, Has.Count.EqualTo(1), "Dispose must seal the in-flight per-tx envelope");

        JToken entry = result[0]!;
        Assert.That((bool)entry["result"]!["failed"]!, Is.True);
        Assert.That((string)entry["txHash"]!, Is.EqualTo(tx.Hash!.ToString()).IgnoreCase, "txHash must be present on every entry, including those closed by Dispose");
    }

    [Test]
    public void GethLikeBlockEnvelopeStreamingTracer_WhenDisposeFailsToCloseEnvelope_DoesNotRetryOnSecondDispose()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;

        ArrayBufferWriter<byte> outerBuffer = new();
        using Utf8JsonWriter jsonWriter = new(outerBuffer);
        GethLikeBlockEnvelopeStreamingTracer tracer = new(GethTraceOptions.Default, jsonWriter, pipeWriter: null, CancellationToken.None);

        jsonWriter.WriteStartArray();
        ((Evm.Tracing.IBlockTracer)tracer).StartNewTxTrace(tx);
        jsonWriter.WriteStartObject();

        Action firstDispose = tracer.Dispose;
        Assert.That(firstDispose, Throws.TypeOf<InvalidOperationException>());

        Action secondDispose = tracer.Dispose;
        Assert.That(secondDispose, Throws.Nothing, "Dispose should not attempt to close the same malformed envelope twice");
    }

    [Test]
    public async Task GethLikeTxTraceStreamingBlockResult_WhenCancelledMidBlock_ClosesOuterArray()
    {
        using CancellationTokenSource requestCts = new();
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingBlockResult result = new(
            (writer, _, ct) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result"u8);
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WritePropertyName("txHash"u8);
                writer.WriteStringValue(TestItem.KeccakA.ToString());
                writer.WriteEndObject();

                requestCts.Cancel();
                ct.ThrowIfCancellationRequested();
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();

        Assert.That(async () => await result.WriteToAsync(pipe.Writer, requestCts.Token), Throws.Nothing,
            "cancellation is swallowed so the response can close cleanly");

        await pipe.Writer.CompleteAsync();
        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Action parse = () => JToken.Parse(body);
        Assert.That(parse, Throws.Nothing, "the finally block must close the outer array even when cancellation fires");

        JArray parsed = (JArray)JToken.Parse(body);
        Assert.That(parsed, Has.Count.EqualTo(1), "the one entry written before cancellation must be present in a well-formed array");
    }

    [Test]
    public async Task GethLikeTxTraceStreamingBlockResult_WhenTraceThrows_EmitsSentinelErrorEntry()
    {
        CancellationTokenSource timeoutCts = new();

        using GethLikeTxTraceStreamingBlockResult result = new(
            (writer, _, _) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result"u8);
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WritePropertyName("txHash"u8);
                writer.WriteStringValue(TestItem.KeccakA.ToString());
                writer.WriteEndObject();

                throw new InvalidOperationException("simulated mid-block failure");
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());

        Pipe pipe = new();
        await result.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        JArray parsed = (JArray)JToken.Parse(body);
        Assert.That(parsed, Has.Count.EqualTo(2), "the array must contain the one good entry plus a sentinel error entry so clients can detect the mid-block failure");

        JToken sentinel = parsed[1]!;
        Assert.That((string)sentinel["error"]!, Does.Contain("simulated mid-block failure"), "the sentinel must carry the human-readable failure reason");
        Assert.That((int)sentinel["errorCode"]!, Is.EqualTo(ErrorCodes.InternalError), "the sentinel must carry the Geth-equivalent errorCode (generic exceptions map to InternalError)");
    }
}
