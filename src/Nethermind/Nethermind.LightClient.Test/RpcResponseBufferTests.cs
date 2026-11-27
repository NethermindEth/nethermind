// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Text.Json;
using Nethermind.JsonRpc;
using Nethermind.LightClient.Rpc;

namespace Nethermind.LightClient.Test;

public class RpcResponseBufferTests
{
    [Test]
    public async Task Payload_limit_preserves_the_id_and_leaves_capacity_for_following_requests()
    {
        using RpcResponseBuffer sink = new(RpcEndpoint.BatchResponseLimit, RpcEndpoint.BatchLimitMessage);
        using ResultWrapper<string> oversized = ResultWrapper<string>.Success(new string('x', BoundedJson.ResultLimit));
        oversized.Id = new JsonRpcId(7);
        using ResultWrapper<string> next = ResultWrapper<string>.Success("next");
        next.Id = new JsonRpcId(8);
        await sink.BeginBatchAsync(CancellationToken.None);
        await sink.WriteBatchItemAsync(oversized, new RpcReport("eth_getProof", 0, true), CancellationToken.None);
        await sink.WriteBatchItemAsync(next, new RpcReport("eth_chainId", 0, true), CancellationToken.None);
        await sink.EndBatchAsync(CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(sink.WrittenMemory);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sink.StopRequested, Is.False);
            Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(2));
            Assert.That(document.RootElement[0].GetProperty("id").GetInt32(), Is.EqualTo(7));
            Assert.That(document.RootElement[0].GetProperty("error").GetProperty("message").GetString(), Does.Contain("8 MiB"));
            Assert.That(document.RootElement[1].GetProperty("result").GetString(), Is.EqualTo("next"));
        }
    }

    [Test]
    public async Task A_partially_written_result_is_rewound_without_touching_other_batch_items()
    {
        using RpcResponseBuffer sink = new(RpcEndpoint.BatchResponseLimit, RpcEndpoint.BatchLimitMessage);
        using ResultWrapper<string> first = ResultWrapper<string>.Success("first");
        using PartialResult partial = new() { Id = new JsonRpcId(7) };
        using ResultWrapper<string> last = ResultWrapper<string>.Success("last");
        RpcReport success = new("eth_getLogs", 0, true);

        await sink.BeginBatchAsync(CancellationToken.None);
        await sink.WriteBatchItemAsync(first, success, CancellationToken.None);
        await sink.WriteBatchItemAsync(partial, success, CancellationToken.None);
        await sink.WriteBatchItemAsync(last, success, CancellationToken.None);
        await sink.EndBatchAsync(CancellationToken.None);
        JsonElement items = JsonElement.Parse(sink.WrittenMemory.Span);
        Assert.That(items.GetArrayLength(), Is.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(items[0].GetProperty("result").GetString(), Is.EqualTo("first"));
            Assert.That(items[1].GetProperty("id").GetInt32(), Is.EqualTo(7));
            Assert.That(items[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.LimitExceeded));
            Assert.That(items[2].GetProperty("result").GetString(), Is.EqualTo("last"));
        }
    }

    private sealed class PartialResult : JsonRpcResponse, IJsonRpcRawResponse
    {
        public void WriteRaw(IBufferWriter<byte> writer)
        {
            writer.Write("{\"jsonrpc\":\"2.0\",\"result\":["u8);
            throw new RpcException(ErrorCodes.LimitExceeded, "Oversized test result.");
        }
    }

    [Test]
    public async Task A_full_response_followed_only_by_notifications_does_not_append_an_error()
    {
        int calls = 0;
        IJsonRpcProcessor processor = RpcTestDriver.CreateProcessor((_, _, _) =>
        {
            calls++;
            return Task.FromResult<object>(new string('x', 820));
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        using RpcResponseBuffer sink = new(1024, "Response limit.");
        await processor.ProcessAsync("""
            [{"jsonrpc":"2.0","method":"first","id":1},{"jsonrpc":"2.0","method":"notification"}]
            """u8.ToArray(), new(Nethermind.JsonRpc.Modules.RpcEndpoint.Http) { BatchLimits = new(32, 128) },
            sink, new(JsonRpcInputMode.SingleDocument));
        using JsonDocument document = JsonDocument.Parse(sink.WrittenMemory);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(document.RootElement[0].GetProperty("result").GetString(), Has.Length.EqualTo(820));
        }
    }

    [Test]
    public async Task Oversized_success_is_replaced_without_accepting_its_result([Values] bool oversizedId)
    {
        const int limit = 1024;
        int accepted = 0;
        using RpcResponseBuffer sink = new(limit, "Response limit.", (_, _) => accepted++);
        using ResultWrapper<string> response = ResultWrapper<string>.Success(new string('x', 2 * limit));
        response.Id = oversizedId ? new JsonRpcId(new string('i', limit)) : new JsonRpcId(7);

        await sink.WriteSingleAsync(response, new RpcReport("eth_subscribe", 0, true), CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(sink.WrittenMemory);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.Zero);
            Assert.That(sink.BytesWritten, Is.LessThanOrEqualTo(limit));
            Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.LimitExceeded));
            if (oversizedId) Assert.That(document.RootElement.GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            else Assert.That(document.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(7));
        }
    }

    [Test]
    public async Task Unanswerable_batch_id_yields_final_error_before_dispatch()
    {
        const int limit = 1024;
        using RpcResponseBuffer sink = new(limit, "Response limit.");
        JsonRpcRequest request = new() { Id = new JsonRpcId(new string('i', limit)) };
        using JsonRpcErrorResponse? final = sink.GetBatchLimitResponse(request);
        Assert.That(final, Is.Not.Null);
        await sink.BeginBatchAsync(CancellationToken.None);
        await sink.WriteBatchItemAsync(final!, RpcReport.Error, CancellationToken.None);
        await sink.EndBatchAsync(CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(sink.WrittenMemory);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sink.BytesWritten, Is.LessThanOrEqualTo(limit));
            Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(document.RootElement[0].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
    }
}
