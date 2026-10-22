// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedSubscriptionsTests
{
    private static readonly Address Address = new("0x1234567890123456789012345678901234567890");
    private static readonly Hash256 Topic = Keccak.Compute("subscription topic");

    [Test]
    public async Task Subscription_response_precedes_authenticated_head_and_unsubscribe_stops_notifications()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        JsonElement response = await socket.Request("eth_subscribe", "newHeads");
        string id = response.GetProperty("result").GetString()!;
        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        harness.Publish(next);
        JsonElement notification = await socket.Next();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notification.GetProperty("method").GetString(), Is.EqualTo("eth_subscription"));
            Assert.That(notification.GetProperty("params").GetProperty("subscription").GetString(), Is.EqualTo(id));
            JsonElement head = notification.GetProperty("params").GetProperty("result");
            Assert.That(head.GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
            Assert.That(head.GetProperty("number").GetString(), Is.EqualTo("0xb"));
            Assert.That(head.TryGetProperty("transactions", out _), Is.False);
        }
        Assert.That((await socket.Request("eth_unsubscribe", id)).GetProperty("result").GetBoolean(), Is.True);
        Assert.That((await socket.Request("eth_unsubscribe", id)).GetProperty("result").GetBoolean(), Is.False);
        harness.Publish(harness.Source.Add(12, next, 1));
        Assert.That((await socket.Request("eth_chainId")).GetProperty("result").GetString(), Is.EqualTo("0x1"));
        Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task Head_notifications_preserve_fields_without_downloading_a_body(
        [Values] bool logs, [Values(1_700_000_000UL, 1_800_000_000UL)] ulong timestamp)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        if (logs) await socket.Request("eth_subscribe", "logs");
        harness.Source.RejectBlocks = !logs;
        Block next = harness.Source.Add(11, harness.Source.Current, 1, timestamp: timestamp);

        harness.Publish(next);

        if (logs) AssertLog(await socket.Next(), next.Hash!, false);
        JsonElement head = (await socket.Next()).GetProperty("params").GetProperty("result");
        JsonElement block = BoundedJson.ToElement(new BlockForRpc(next, false, MainnetSpecProvider.Instance));
        Dictionary<string, string> expected = block.EnumerateObject()
            .Where(property => property.Name is not ("transactions" or "uncles" or "withdrawals" or "size" or "totalDifficulty"))
            .ToDictionary(property => property.Name, property => property.Value.GetRawText());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(head.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetRawText()), Is.EquivalentTo(expected));
            Assert.That(harness.Source.HeaderReads, Is.EqualTo(1), "the authenticated header is fetched once");
            Assert.That(harness.Source.BlockReads, Is.EqualTo(logs ? 1 : 0), "only logs require a body");
            Assert.That(harness.Source.ReceiptReads, Is.EqualTo(logs ? 1 : 0));
        }
    }

    [Test]
    public async Task Subscription_logs_wait_for_external_admission_while_new_external_reads_are_rejected([Values] bool cancel)
    {
        using Source source = new();
        using VerifiedRpc rpc = new(source, () => Harness.Head(source.Current), 1, specProvider: MainnetSpecProvider.Instance);
        await using ExecutionLoad load = new();
        await load.Entered;
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { source.Current.Hash!.ToString(), false });
        using CancellationTokenSource cancellation = new();

        Task<JsonElement> logs = rpc.GetSubscriptionLogsAsync(source.Current.Header, cancellation.Token);
        Assert.That(logs.IsCompleted, Is.False);
        RpcException? rejected = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getBlockByHash", parameters, CancellationToken.None));
        Assert.That(rejected!.Code, Is.EqualTo(-32005));
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.ThatAsync(async () => { await logs; }, Throws.InstanceOf<OperationCanceledException>());
        }
        await load.ReleaseAsync();

        if (!cancel)
        {
            JsonElement result = await logs.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(result[0].GetProperty("blockHash").GetString(), Is.EqualTo(source.Current.Hash!.ToString()));
        }
    }

    [Test]
    public async Task External_execution_reads_do_not_disconnect_subscriptions_or_erase_reorganization_history()
    {
        await using Harness harness = new();
        Block common = harness.Source.Current;
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        await socket.Request("eth_subscribe", "logs");
        Block first = harness.Source.Add(11, common, 1);
        harness.Publish(first);
        AssertLog(await socket.Next(), first.Hash!, false);
        await socket.Next();

        await using ExecutionLoad load = new();
        await load.Entered;
        TaskCompletionSource headerRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Block second = harness.Source.Add(12, first, 1);
        harness.Source.HeaderRequested = header => { if (header.Hash == second.Hash) headerRequested.TrySetResult(); };
        harness.Publish(second);
        await headerRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await load.ReleaseAsync();
        AssertLog(await socket.Next(), second.Hash!, false);
        Assert.That((await socket.Next()).GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(second.Hash!.ToString()));

        Block replacement = harness.Source.Add(11, common, 2);
        harness.Publish(replacement);
        AssertLog(await socket.Next(), second.Hash!, true);
        AssertLog(await socket.Next(), first.Hash!, true);
        AssertLog(await socket.Next(), replacement.Hash!, false);
        Assert.That((await socket.Next()).GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(replacement.Hash!.ToString()));
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
    }

    [Test]
    public async Task Publisher_cancellation_or_failure_releases_execution_admission(
        [Values("deadline", "shutdown", "receipts")] string termination)
    {
        WriteClock clock = new();
        await using Harness harness = new(clock);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        await using (ExecutionLoad load = new())
        {
            await load.Entered;
            clock.ObservePublication = true;
            if (termination == "receipts") harness.Source.TamperReceipts = true;
            harness.Publish(harness.Source.Add(11, harness.Source.Current, 1));
            await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (termination == "deadline") clock.Expire();
            else if (termination == "shutdown") await harness.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else await load.ReleaseAsync();

            await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
        }

        await using ExecutionLoad subsequent = new();
        await subsequent.Entered;
    }

    [Test]
    public async Task Logs_from_a_reorganization_are_removed_before_replacement_logs_are_emitted()
    {
        await using Harness harness = new();
        Block common = harness.Source.Current;
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs", new { address = Address.ToString(), topics = new[] { Topic.ToString() } });
        Block first = harness.Source.Add(11, common, 1);
        harness.Publish(first);
        AssertLog(await socket.Next(), first.Hash!, false);
        Block second = harness.Source.Add(12, first, 1);
        harness.Publish(second);
        AssertLog(await socket.Next(), second.Hash!, false);
        Block replacement = harness.Source.Add(11, common, 2);
        harness.Publish(replacement);

        AssertLog(await socket.Next(), second.Hash!, true);
        AssertLog(await socket.Next(), first.Hash!, true);
        AssertLog(await socket.Next(), replacement.Hash!, false);
    }

    [Test]
    public async Task Missing_blocks_are_repaired_from_the_authenticated_head_before_log_delivery()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        Block first = harness.Source.Add(11, harness.Source.Current, 1);
        Block second = harness.Source.Add(12, first, 1);
        Block third = harness.Source.Add(13, second, 1);
        harness.Publish(third);

        AssertLog(await socket.Next(), first.Hash!, false);
        AssertLog(await socket.Next(), second.Hash!, false);
        AssertLog(await socket.Next(), third.Hash!, false);
    }

    [Test]
    public async Task Rewinding_to_an_existing_head_emits_a_new_head_notification()
    {
        await using Harness harness = new();
        Block previous = harness.Source.Current;
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        Block next = harness.Source.Add(11, previous, 1);
        harness.Publish(next);
        await socket.Next();
        harness.Publish(previous);

        JsonElement returned = (await socket.Next()).GetProperty("params").GetProperty("result");
        Assert.That(returned.GetProperty("hash").GetString(), Is.EqualTo(previous.Hash!.ToString()));
    }

    [Test]
    public async Task Forged_headers_or_receipts_close_the_stream_before_publishing_invalid_data([Values] bool receipts)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", receipts ? "logs" : "newHeads");
        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        if (receipts) harness.Source.TamperReceipts = true;
        else harness.Source.TamperHeader = true;
        harness.Publish(next);

        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task A_gap_beyond_the_history_bound_closes_the_stream()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        Block next = harness.Source.Current;
        for (ulong number = 11; number <= 43; number++) next = harness.Source.Add(number, next, 1);
        harness.Publish(next);

        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task Exactly_the_history_bound_of_missing_blocks_can_be_repaired()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        List<Block> added = [];
        Block next = harness.Source.Current;
        for (ulong number = 11; number <= 42; number++)
        {
            next = harness.Source.Add(number, next, 1);
            added.Add(next);
        }
        harness.Publish(next);

        foreach (Block block in added) AssertLog(await socket.Next(), block.Hash!, false);
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
    }

    [Test]
    public async Task A_healthy_consumer_receives_a_block_larger_than_the_message_queue()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        Block next = harness.Source.Add(11, harness.Source.Current, 1, logCount: 250);
        harness.Publish(next);

        for (int i = 0; i < 250; i++) AssertLog(await socket.Next(), next.Hash!, false);
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
    }

    [Test]
    public async Task Batch_requests_preserve_notification_and_invalid_envelope_semantics()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        socket.Push(new object[]
        {
            new { jsonrpc = "2.0", method = "eth_chainId", @params = Array.Empty<object>() },
            Request("eth_subscribe", "newHeads"),
            42,
        });

        JsonElement batch = await socket.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.GetArrayLength(), Is.EqualTo(2));
            Assert.That(batch[0].GetProperty("id").GetInt32(), Is.EqualTo(1));
            Assert.That(batch[0].GetProperty("result").GetString(), Does.StartWith("0x"));
            Assert.That(batch[1].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(batch[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
        }
        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        harness.Publish(next);
        Assert.That((await socket.Next()).GetProperty("method").GetString(), Is.EqualTo("eth_subscription"));
    }

    [Test]
    public async Task Unsubscribe_rejects_extra_parameters_and_keeps_the_subscription()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        string id = (await socket.Request("eth_subscribe", "newHeads")).GetProperty("result").GetString()!;

        Assert.That((await socket.Request("eth_unsubscribe", id, false)).GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
        Assert.That((await socket.Request("eth_unsubscribe", "0xunknown")).GetProperty("result").GetBoolean(), Is.False);
        Assert.That((await socket.Request("eth_unsubscribe", id)).GetProperty("result").GetBoolean(), Is.True);
    }

    [Test]
    public async Task A_late_log_subscriber_receives_only_the_replacement_branch()
    {
        await using Harness harness = new();
        Block common = harness.Source.Current;
        Block orphan = harness.Source.Add(11, common, 1);
        harness.Publish(orphan);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        Block replacement = harness.Source.Add(11, common, 2);
        harness.Publish(replacement);

        AssertLog(await socket.Next(), replacement.Hash!, false);
        Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task WebSocket_requests_reject_non_text_or_oversized_messages([Values] bool oversized)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        if (oversized) socket.Push(Request("eth_chainId", new string('x', 64 * 1024)));
        else
        {
            socket.MessageType = WebSocketMessageType.Binary;
            socket.Push(Request("eth_chainId"));
        }

        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task A_batch_stops_before_collecting_further_results_when_its_response_exceeds_the_message_limit()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        harness.Source.Code = new byte[100_000];
        int calls = 0;
        harness.Source.BeforeCodeResponse = count => calls = count;
        socket.Push(Enumerable.Repeat(Request("eth_getCode", Address.ToString(), "latest"), 32).ToArray());

        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
        }
    }

    [TestCase("newPendingTransactions")]
    [TestCase("syncing")]
    public async Task Unsupported_subscription_types_are_rejected(string type)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        Assert.That((await socket.Request("eth_subscribe", type)).GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
    }

    [Test]
    public async Task Subscription_filters_reuse_the_verified_RPC_contract(
        [Values("address", "topics", "fromBlock")] string field)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        object value = field == "topics" ? new[] { "0x1234" } : "0x1234";
        JsonElement response = await socket.Request("eth_subscribe", "logs", new Dictionary<string, object> { [field] = value });

        Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
    }

    [Test]
    public async Task Subscription_and_client_admission_are_bounded()
    {
        await using Harness harness = new();
        FakeSocket first = harness.Connect();
        for (int i = 0; i < VerifiedSubscriptions.MaxSubscriptions; i++)
            Assert.That((await first.Request("eth_subscribe", "newHeads")).TryGetProperty("result", out _), Is.True);
        Assert.That((await first.Request("eth_subscribe", "newHeads")).GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32000));
        for (int i = 1; i < VerifiedSubscriptions.MaxClients; i++)
        {
            FakeSocket socket = harness.Connect();
            await socket.Request("eth_chainId");
        }
        FakeSocket rejected = harness.Connect();
        await harness.Tasks[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Contexts[^1].Response.StatusCode, Is.EqualTo(429));
            Assert.That(rejected.Accepted, Is.False);
        }
    }

    [Test]
    public async Task A_slow_consumer_is_disconnected_when_its_full_queue_cannot_drain_before_the_deadline()
    {
        WriteClock clock = new();
        await using Harness harness = new(clock);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_chainId");
        socket.StallAfter = 1;
        harness.Source.Code = new byte[100_000];
        harness.Source.BeforeCodeResponse = count => { if (count == 7) clock.ObserveWrites = true; };
        for (int i = 0; i < 16; i++) socket.Push(Request("eth_getCode", Address.ToString(), "latest"));

        await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Expire();
        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(socket.Sends, Is.EqualTo(2), "one successful reply and one stalled reply, followed by a full queue that misses its deadline");
    }

    [Test]
    public async Task Shutdown_joins_connections_and_disposes_their_sockets()
    {
        Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");

        await harness.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(socket.Disposed, Is.True);
            Assert.That(harness.Tasks, Has.All.Property(nameof(Task.IsCompleted)).True);
        }
    }

    private static object Request(string method, params object[] parameters) => new { jsonrpc = "2.0", id = 1, method, @params = parameters };

    private static void AssertLog(JsonElement notification, Hash256 hash, bool removed)
    {
        JsonElement log = notification.GetProperty("params").GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.GetProperty("blockHash").GetString(), Is.EqualTo(hash.ToString()));
            Assert.That(log.GetProperty("removed").GetBoolean(), Is.EqualTo(removed));
            Assert.That(log.GetProperty("address").GetString(), Is.EqualTo(Address.ToString()));
            Assert.That(log.GetProperty("topics")[0].GetString(), Is.EqualTo(Topic.ToString()));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal Source Source { get; } = new();
        private readonly VerifiedRpc _rpc;
        private readonly VerifiedSubscriptions _subscriptions;
        internal List<Task> Tasks { get; } = [];
        internal List<HttpContext> Contexts { get; } = [];

        internal Harness(TimeProvider? clock = null)
        {
            _rpc = new(Source, () => Head(Source.Current), 1, specProvider: MainnetSpecProvider.Instance, getLatestHead: () => Head(Source.Current));
            _subscriptions = new(Source, _rpc, NullLogger.Instance, clock);
            _subscriptions.PublishHead(Head(Source.Current));
        }

        internal FakeSocket Connect()
        {
            FakeSocket socket = new();
            DefaultHttpContext context = new();
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            Contexts.Add(context);
            Tasks.Add(_subscriptions.HandleAsync(context));
            return socket;
        }

        internal void Publish(Block block)
        {
            Source.Current = block;
            _subscriptions.PublishHead(Head(block));
        }

        internal Task StopAsync() => _subscriptions.DisposeAsync().AsTask();

        public async ValueTask DisposeAsync()
        {
            await _subscriptions.DisposeAsync();
            await Task.WhenAll(Tasks);
            _rpc.Dispose();
            Source.Dispose();
        }

        internal static VerifiedHead Head(Block block) => new(100, block.Number, block.Hash!, block.StateRoot!);
    }

    private sealed class ExecutionLoad : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Source _source;
        private readonly VerifiedRpc _rpc;
        private readonly Task<object>[] _requests;
        internal Task Entered => _source.BlocksStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal ExecutionLoad()
        {
            _source = new() { BlockGate = _release };
            _rpc = new(_source, () => Harness.Head(_source.Current), 1, specProvider: MainnetSpecProvider.Instance,
                getLatestHead: () => Harness.Head(_source.Current));
            JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { _source.Current.Hash!.ToString(), false });
            _requests = Enumerable.Range(0, 4).Select(_ => _rpc.InvokeAsync("eth_getBlockByHash", parameters, CancellationToken.None)).ToArray();
        }

        internal async Task ReleaseAsync()
        {
            _source.BlockGate = null;
            _release.TrySetResult();
            await Task.WhenAll(_requests);
        }

        public async ValueTask DisposeAsync()
        {
            try { await ReleaseAsync(); }
            finally
            {
                _rpc.Dispose();
                _source.Dispose();
            }
        }
    }

    private sealed class WriteClock : TimeProvider
    {
        internal bool ObserveWrites { get; set; }
        internal bool ObservePublication { get; set; }
        internal TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Timer? _timer;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(callback, state);
            if (ObserveWrites && dueTime == TimeSpan.FromSeconds(10) || ObservePublication && dueTime == TimeSpan.FromSeconds(90))
            {
                _timer = timer;
                Created.TrySetResult();
            }
            return timer;
        }

        internal void Expire() => _timer!.Expire();

        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal void Expire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class SocketFeature(FakeSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            socket.Accepted = true;
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        internal Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _pending;
        private int _offset;
        private WebSocketState _state = WebSocketState.Open;
        internal bool Accepted { get; set; }
        internal bool Disposed { get; private set; }
        internal int StallAfter { get; set; } = int.MaxValue;
        internal int Sends { get; private set; }
        internal WebSocketMessageType MessageType { get; set; } = WebSocketMessageType.Text;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        internal void Push(object request) => _input.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(request));
        internal async Task<JsonElement> Request(string method, params object[] parameters)
        {
            Push(VerifiedSubscriptionsTests.Request(method, parameters));
            return await Next();
        }
        internal async Task<JsonElement> Next()
        {
            byte[] message = await Output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using JsonDocument document = JsonDocument.Parse(message);
            return document.RootElement.Clone();
        }
        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _input.Writer.TryComplete();
            Aborted.TrySetResult();
        }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override void Dispose() { Disposed = true; Abort(); }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            try { _pending ??= await _input.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { throw new WebSocketException("Closed test socket"); }
            int count = Math.Min(buffer.Count, _pending.Length - _offset);
            _pending.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            bool end = _offset == _pending.Length;
            if (end) { _pending = null; _offset = 0; }
            return new(count, MessageType, end);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (++Sends > StallAfter) await _resume.Task.WaitAsync(cancellationToken);
            await Output.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }
    }

    private sealed class Source : IExecutionStateSource, IDisposable
    {
        private readonly PrivateKey _key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        private readonly Dictionary<Hash256, (Block Block, TxReceipt[] Receipts)> _blocks = [];
        internal Block Current { get; set; }
        internal bool TamperHeader { get; set; }
        internal bool TamperReceipts { get; set; }
        internal byte[] Code { get; set; } = [];
        internal Action<int>? BeforeCodeResponse { get; set; }
        internal Action<BlockHeader>? HeaderRequested { get; set; }
        internal bool RejectBlocks { get; set; }
        internal TaskCompletionSource? BlockGate { get; set; }
        internal TaskCompletionSource BlocksStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HeaderReads => Volatile.Read(ref _headerReads);
        internal int BlockReads => Volatile.Read(ref _blockReads);
        internal int ReceiptReads => Volatile.Read(ref _receiptReads);
        private int _codeCalls;
        private int _headerReads;
        private int _blockReads;
        private int _receiptReads;
        private int _blockedBlockReads;

        internal Source() => Current = Add(10, null, 0);

        internal Block Add(ulong number, Block? parent, byte branch, int logCount = 1, ulong timestamp = 1_700_000_000)
        {
            Transaction transaction = new() { Nonce = number, GasLimit = 500_000, GasPrice = 2, To = Address, Value = 3 };
            new EthereumEcdsa(1).Sign(_key, transaction, isEip155Enabled: true);
            transaction.Hash = transaction.CalculateHash();
            LogEntry[] logs = new LogEntry[logCount];
            for (int i = 0; i < logs.Length; i++) logs[i] = new(Address, [branch, (byte)number], [Topic]);
            TxReceipt receipt = new()
            {
                TxType = transaction.Type,
                StatusCode = 1,
                GasUsedTotal = (ulong)(21_000 + 1_000 * logCount),
                Logs = logs,
            };
            BlockHeader header = new(parent?.Hash ?? Hash256.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero,
                UInt256.Zero, number, 30_000_000, timestamp + number, [branch])
            {
                StateRoot = Keccak.Compute("state" + number + ":" + branch),
                TxRoot = TxTrie.CalculateRoot([transaction]),
                WithdrawalsRoot = Keccak.EmptyTreeHash,
                GasUsed = receipt.GasUsedTotal,
                BaseFeePerGas = 17,
            };
            IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(header);
            if (spec.IsEip4844Enabled)
            {
                header.BlobGasUsed = 0;
                header.ExcessBlobGas = 131_072;
            }
            if (spec.IsEip4788Enabled) header.ParentBeaconBlockRoot = Keccak.Compute("beacon" + number);
            if (spec.IsEip6110Enabled) header.RequestsHash = Keccak.OfAnEmptySequenceRlp;
            if (spec.IsEip7928Enabled) header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
            if (spec.IsEip7843Enabled) header.SlotNumber = number;
            header.ReceiptsRoot = ReceiptTrie.CalculateRoot(spec, [receipt], new ReceiptMessageDecoder());
            header.Hash = Keccak.Compute(Rlp.Encode(header).Bytes);
            Block block = new(header, new BlockBody([transaction], [], []));
            _blocks.Add(header.Hash, (block, [receipt]));
            return block;
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => Task.FromResult(new Account(0, 0, Keccak.EmptyTreeHash, Keccak.Compute(Code)));
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) => Task.FromResult(UInt256.Zero);
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            BeforeCodeResponse?.Invoke(++_codeCalls);
            return Task.FromResult(Code);
        }
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _headerReads);
            BlockHeader header = _blocks[head.BlockHash].Block.Header;
            if (TamperHeader) header.Number++;
            HeaderRequested?.Invoke(header);
            return Task.FromResult(header);
        }
        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) => Task.FromResult(_blocks[hash].Block.Header);
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _blockReads);
            if (RejectBlocks) throw new InvalidOperationException("A header subscription must not request a block body.");
            if (BlockGate is { } gate)
            {
                if (Interlocked.Increment(ref _blockedBlockReads) == 4) BlocksStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
            }
            Block block = _blocks[header.Hash!].Block;
            ExecutionPeerTransport.VerifyBlockBody(header, block.Body);
            return block;
        }
        public Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _receiptReads);
            TxReceipt[] receipts = _blocks[block.Hash!].Receipts;
            if (TamperReceipts) receipts[0].Logs![0].Data[0] ^= 1;
            ExecutionPeerTransport.VerifyReceipts(block.Header, receipts, MainnetSpecProvider.Instance);
            return Task.FromResult(receipts);
        }
        public void Dispose() => _key.Dispose();
    }
}
