// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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

        GethLikeTxTraceStreamingSingleResult result = new(
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

        Func<Task> writeAct = async () => await result.WriteToAsync(pipe.Writer, requestCts.Token);
        await writeAct.Should().NotThrowAsync("WriteToAsync swallows OperationCanceledException so the HTTP layer can close the response cleanly");

        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Action parse = () => JToken.Parse(body);
        parse.Should().NotThrow("the finally block must close the structLogs array and the outer object even when cancellation fires");

        JToken parsed = JToken.Parse(body);
        parsed["failed"]!.Value<bool>().Should().BeTrue("a cancelled mid-trace is reported as failed");
    }

    [Test]
    public void GethLikeTxTraceStreamingSingleResult_WhenTraceThrows_WritesErrorAndErrorCode()
    {
        CancellationTokenSource timeoutCts = new();

        GethLikeTxTraceStreamingSingleResult result = new(
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

        ArrayBufferWriter bufferWriter = new();
        using Utf8JsonWriter writer = new(bufferWriter);
        result.WriteAsJson(writer);
        writer.Flush();

        JToken parsed = JToken.Parse(Encoding.UTF8.GetString(bufferWriter.WrittenSpan));
        parsed["failed"]!.Value<bool>().Should().BeTrue();
        parsed["error"]!.Value<string>().Should().Contain("simulated mid-trace failure");
        parsed["errorCode"]!.Value<int>().Should().Be(ErrorCodes.InternalError, "generic exceptions map to InternalError; InsufficientBalanceException maps to InvalidInput");
    }

    [Test]
    public void GethLikeBlockEnvelopeStreamingTracer_WhenDisposedMidTx_EmitsTxHashAndClosesEnvelope()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;

        ArrayBufferWriter outerBuffer = new();
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
        result.Should().HaveCount(1, "Dispose must seal the in-flight per-tx envelope");

        JToken entry = result[0]!;
        ((bool)entry["result"]!["failed"]!).Should().BeTrue();
        ((string)entry["txHash"]!).Should().BeEquivalentTo(tx.Hash!.ToString(), "txHash must be present on every entry, including those closed by Dispose");
    }

    [Test]
    public async Task GethLikeTxTraceStreamingBlockResult_WhenCancelledMidBlock_ClosesOuterArray()
    {
        using CancellationTokenSource requestCts = new();
        CancellationTokenSource timeoutCts = new();

        GethLikeTxTraceStreamingBlockResult result = new(
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

        Func<Task> writeAct = async () => await result.WriteToAsync(pipe.Writer, requestCts.Token);
        await writeAct.Should().NotThrowAsync("cancellation is swallowed so the response can close cleanly");

        await pipe.Writer.CompleteAsync();
        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Action parse = () => JToken.Parse(body);
        parse.Should().NotThrow("the finally block must close the outer array even when cancellation fires");

        JArray parsed = (JArray)JToken.Parse(body);
        parsed.Should().HaveCount(1, "the one entry written before cancellation must be present in a well-formed array");
    }

    private sealed class ArrayBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[256];
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            int needed = Math.Max(sizeHint, 1);
            if (_buffer.Length - _written >= needed) return;
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + needed));
        }
    }
}
