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
using Nethermind.Core.Test.Builders;
using Nethermind.State;
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

    [Test]
    public async Task Validation_failure_replaces_only_uncommitted_response([Values(0, 1, 2)] int commitMode)
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        using JsonRpcSuccessResponse response = new()
        {
            Id = new JsonRpcId(42L), Result = new InvalidTransactionResult(commitMode)
        };

        if (commitMode != 2)
        {
            await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None);
        }
        else
        {
            Assert.ThrowsAsync<InsufficientBalanceException>(async () =>
                await JsonRpcResponseWriter.WriteAsync(pipe.Writer, response, new JsonSerializerOptions(), CancellationToken.None));
        }
        await pipe.Writer.CompleteAsync();
        ReadResult read = await pipe.Reader.ReadAsync();
        string envelope = Encoding.UTF8.GetString(read.Buffer.ToArray());
        await pipe.Reader.CompleteAsync();

        if (commitMode != 2)
        {
            using JsonDocument document = JsonDocument.Parse(envelope);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(document.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(42));
                Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidInput));
                Assert.That(document.RootElement.TryGetProperty("result", out _), Is.False);
            }
        }
        else
        {
            Assert.That(envelope, Does.StartWith("{\"jsonrpc\":\"2.0\",\"result\":"));
            Assert.That(envelope, Does.Not.Contain("\"error\""));
        }
    }

    private sealed class InvalidTransactionResult(int commitMode) : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("{\"vmTrace\":"u8);
            if (commitMode == 1) await writer.FlushAsync(cancellationToken);
            if (commitMode == 2) writer.Write(new byte[20_000]);
            throw new InsufficientBalanceException(TestItem.AddressA);
        }
    }

    [Test]
    public void Fractional_decimal_id_is_rejected_at_construction()
    {
        Action act = () => new JsonRpcId(1.5m);

        Assert.That(act, Throws.TypeOf<NotSupportedException>());
    }
}
