// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.LightClient.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class RpcSubscriptionsTests
{
    private static readonly Address Address = new("0x1234567890123456789012345678901234567890");
    private static readonly Hash256 Topic = Keccak.Compute("subscription topic");

    [Test]
    public async Task Http_polling_reports_verified_ancestors_and_removals_and_keeps_separate_cursors()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        string blockFilter = (await harness.HttpRequest("eth_newBlockFilter", [])).GetProperty("result").GetString()!;
        string logFilter = (await harness.HttpRequest("eth_newFilter", [new { address = Address.ToString(), topics = new[] { Topic.ToString() } }]))
            .GetProperty("result").GetString()!;
        string unmatched = (await harness.HttpRequest("eth_newFilter", [new { address = Address.Zero.ToString() }]))
            .GetProperty("result").GetString()!;

        Block common = harness.Source.Current;
        Block first = harness.Source.Add(11, common, 1);
        Block second = harness.Source.Add(12, first, 1);
        harness.Publish(second);
        AssertHead(await socket.Next(), second);
        JsonElement blocks = (await harness.HttpRequest("eth_getFilterChanges", [blockFilter])).GetProperty("result");
        JsonElement logs = (await harness.HttpRequest("eth_getFilterChanges", [logFilter])).GetProperty("result");
        JsonElement unmatchedLogs = (await harness.HttpRequest("eth_getFilterChanges", [unmatched])).GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.EnumerateArray().Select(hash => hash.GetString()), Is.EqualTo(new[] { first.Hash!.ToString(), second.Hash!.ToString() }));
            Assert.That(logs.EnumerateArray().Select(LogIdentity), Is.EqualTo(new[] { (first.Hash!.ToString(), false), (second.Hash!.ToString(), false) }));
            Assert.That(unmatchedLogs.GetArrayLength(), Is.Zero);
        }

        Block replacement = harness.Source.Add(11, common, 2);
        harness.Publish(replacement);
        AssertHead(await socket.Next(), replacement);
        JsonElement current = (await harness.HttpRequest("eth_getFilterLogs", [logFilter])).GetProperty("result");
        JsonElement changes = (await harness.HttpRequest("eth_getFilterChanges", [logFilter])).GetProperty("result");
        JsonElement consumed = (await harness.HttpRequest("eth_getFilterChanges", [logFilter])).GetProperty("result");
        JsonElement uninstalled = (await harness.HttpRequest("eth_uninstallFilter", [logFilter])).GetProperty("result");
        JsonElement missing = (await harness.HttpRequest("eth_getFilterChanges", [logFilter])).GetProperty("error");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.EnumerateArray().Select(LogIdentity), Is.EqualTo(new[] { (replacement.Hash!.ToString(), false) }));
            Assert.That(changes.EnumerateArray().Select(LogIdentity), Is.EqualTo(new[]
            {
                (second.Hash!.ToString(), true), (first.Hash!.ToString(), true), (replacement.Hash!.ToString(), false),
            }));
            Assert.That(consumed.GetArrayLength(), Is.Zero);
            Assert.That(uninstalled.GetBoolean(), Is.True);
            Assert.That(missing.GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.InvalidInput));
        }

        static (string?, bool) LogIdentity(JsonElement log) => (log.GetProperty("blockHash").GetString(), log.GetProperty("removed").GetBoolean());
    }

    [Test]
    public async Task Cancelled_filter_poll_keeps_changes_for_retry_and_does_not_admit_a_concurrent_poll()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        string id = (await harness.HttpRequest("eth_newFilter", [new { }])).GetProperty("result").GetString()!;
        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        harness.Publish(next);
        AssertHead(await socket.Next(), next);

        harness.Source.BlockGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        Task<JsonElement> first = harness.HttpRequest("eth_getFilterChanges", [id], cancellation.Token);
        await harness.Source.FirstBodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement concurrent = (await harness.HttpRequest("eth_getFilterChanges", [id])).GetProperty("error");
        await cancellation.CancelAsync();
        await Assert.ThatAsync(async () => { await first; }, Throws.InstanceOf<OperationCanceledException>());
        harness.Source.BlockGate = null;
        JsonElement retry = (await harness.HttpRequest("eth_getFilterChanges", [id])).GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(concurrent.GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.LimitExceeded));
            Assert.That(retry.GetArrayLength(), Is.EqualTo(1));
            Assert.That(retry[0].GetProperty("blockHash").GetString(), Is.EqualTo(next.Hash!.ToString()));
        }
    }

    [Test]
    public async Task Polling_overflow_preserves_changes_when_http_can_serve_them([Values] bool httpOverflow)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        string id = (await harness.HttpRequest("eth_newFilter", [new { }])).GetProperty("result").GetString()!;
        int blockCount = httpOverflow ? 30 : 1;
        for (int i = 0; i < blockCount; i++)
        {
            Block next = harness.Source.Add((ulong)(11 + i), harness.Source.Current, 1, logCount: 1_000);
            harness.Publish(next);
            AssertHead(await socket.Next(), next);
        }

        JsonElement tooLarge = (await socket.Request("eth_getFilterChanges", id)).GetProperty("error");
        JsonElement http = await harness.HttpRequest("eth_getFilterChanges", [id]);
        if (httpOverflow)
        {
            JsonElement removed = (await harness.HttpRequest("eth_getFilterChanges", [id])).GetProperty("error");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tooLarge.GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.LimitExceeded));
                Assert.That(http.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.LimitExceeded));
                Assert.That(removed.GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.InvalidInput));
            }
            return;
        }
        JsonElement changes = http.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooLarge.GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.LimitExceeded));
            Assert.That(changes.GetArrayLength(), Is.EqualTo(1_000));
            Assert.That(changes[0].GetProperty("logIndex").GetString(), Is.EqualTo("0x0"));
            Assert.That(changes[999].GetProperty("logIndex").GetString(), Is.EqualTo("0x3e7"));
        }
    }

    [Test]
    public async Task Http_filter_parameters_reject_invalid_ranges_and_filter_kinds()
    {
        await using Harness harness = new();
        string id = (await harness.HttpRequest("eth_newBlockFilter", [])).GetProperty("result").GetString()!;
        JsonElement[] results = await Task.WhenAll(
            harness.HttpRequest("eth_newFilter", [new { fromBlock = "0xc", toBlock = "0xb" }]),
            harness.HttpRequest("eth_newFilter", [new { blockHash = harness.Source.Current.Hash!.ToString(), toBlock = "latest" }]),
            harness.HttpRequest("eth_getFilterChanges", ["0x01"]),
            harness.HttpRequest("eth_getFilterLogs", [id]),
            harness.HttpRequest("eth_uninstallFilter", ["0x0"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[..3].Select(result => result.GetProperty("error").GetProperty("code").GetInt32()),
                Is.All.EqualTo(Nethermind.JsonRpc.ErrorCodes.InvalidParams));
            Assert.That(results[3].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(Nethermind.JsonRpc.ErrorCodes.InvalidInput));
            Assert.That(results[4].GetProperty("result").GetBoolean(), Is.False);
        }
    }

    [Test]
    public async Task Metrics_count_websocket_methods_subscriptions_batch_notifications_and_errors()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        await using Harness harness = new(metrics: metrics);
        FakeSocket socket = harness.Connect();
        string id = (await socket.Request("eth_subscribe", "newHeads")).GetProperty("result").GetString()!;
        await socket.Request("eth_unsubscribe", id);
        await socket.Request("eth_chainId");
        socket.Push(new object[]
        {
            new { jsonrpc = "2.0", method = "eth_chainId", @params = Array.Empty<object>() },
            Request("private_unknown_method"),
            false,
        });

        JsonElement batch = await socket.Next();
        string scrape = await LightClientMetricsTests.ScrapeAsync(metrics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.GetArrayLength(), Is.EqualTo(2));
            LightClientMetricsTests.AssertRpc(scrape, "eth_subscribe", "ok", 1);
            LightClientMetricsTests.AssertRpc(scrape, "eth_unsubscribe", "ok", 1);
            LightClientMetricsTests.AssertRpc(scrape, "eth_chainId", "ok", 2);
            LightClientMetricsTests.AssertRpc(scrape, "other", "error", 2);
            Assert.That(scrape, Does.Not.Contain("private_unknown_method"));
        }
    }

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
        JsonElement chainId = await socket.Request("eth_chainId");
        bool unexpectedNotification = socket.Output.Reader.TryRead(out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainId.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(unexpectedNotification, Is.False);
        }
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
    public async Task Subscription_logs_have_capacity_reserved_from_external_admission([Values] bool cancel)
    {
        using Source source = new();
        using RpcBackend rpc = new(source, () => Harness.Head(source.Current), 1, specProvider: MainnetSpecProvider.Instance);
        await using ExecutionLoad load = new();
        await load.Entered;
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { source.Current.Hash!.ToString(), false });
        using CancellationTokenSource cancellation = new();
        if (cancel) await cancellation.CancelAsync();

        Task<JsonElement> logs = rpc.GetSubscriptionLogsAsync(source.Current.Header, static (_, _) => true, cancellation.Token);
        RpcException? rejected = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getBlockByHash", parameters, CancellationToken.None));
        Assert.That(rejected!.Code, Is.EqualTo(-32005));
        if (cancel)
        {
            await Assert.ThatAsync(async () => { await logs; }, Throws.InstanceOf<OperationCanceledException>());
        }
        else
        {
            JsonElement result = await logs.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(result[0].GetProperty("blockHash").GetString(), Is.EqualTo(source.Current.Hash!.ToString()));
        }
        await load.ReleaseAsync();
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
        JsonElement notification = await socket.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notification.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(replacement.Hash!.ToString()));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }
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
            else harness.Source.BlockGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Publish(harness.Source.Add(11, harness.Source.Current, 1));
            await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (termination != "receipts") await harness.Source.FirstBodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (termination == "deadline") clock.Expire();
            else if (termination == "shutdown") await harness.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else await load.ReleaseAsync();

            await socket.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
            if (termination == "deadline")
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(socket.CloseStatus, Is.EqualTo((WebSocketCloseStatus)1013));
                    Assert.That(socket.CloseStatusDescription, Does.Contain("delivery deadline"));
                }
            }
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
    public async Task Rewinding_keeps_canonical_logs_for_a_later_reorganization([Values] bool heads)
    {
        await using Harness harness = new();
        Block root = harness.Source.Current;
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        if (heads) await socket.Request("eth_subscribe", "newHeads");
        Block first = harness.Source.Add(11, root, 1);
        harness.Publish(first);
        AssertLog(await socket.Next(), first.Hash!, false, "0x010b");
        if (heads) await socket.Next();
        Block second = harness.Source.Add(12, first, 2);
        harness.Publish(second);
        AssertLog(await socket.Next(), second.Hash!, false, "0x020c");
        if (heads) await socket.Next();

        harness.Publish(first);
        AssertLog(await socket.Next(), second.Hash!, true, "0x020c");
        if (heads) await socket.Next();
        Block sibling = harness.Source.Add(11, root, 3);
        harness.Publish(sibling);

        AssertLog(await socket.Next(), first.Hash!, true, "0x010b");
        AssertLog(await socket.Next(), sibling.Hash!, false, "0x030b");
        if (heads) await socket.Next();
        bool unexpectedNotification = socket.Output.Reader.TryRead(out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(unexpectedNotification, Is.False);
        }
    }

    [Test]
    public async Task Returning_to_a_replaced_branch_prunes_its_previous_log_history()
    {
        await using Harness harness = new();
        Block root = harness.Source.Current;
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        Block first = harness.Source.Add(11, root, 1);
        Block second = harness.Source.Add(11, root, 2);
        harness.Publish(first);
        AssertLog(await socket.Next(), first.Hash!, false, "0x010b");
        harness.Publish(second);
        AssertLog(await socket.Next(), first.Hash!, true, "0x010b");
        AssertLog(await socket.Next(), second.Hash!, false, "0x020b");

        harness.Publish(first);

        AssertLog(await socket.Next(), second.Hash!, true, "0x020b");
        AssertLog(await socket.Next(), first.Hash!, false, "0x010b");
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
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

        await socket.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        await socket.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task A_slow_log_reader_cannot_delay_other_clients_heads_or_overflow_their_queues()
    {
        WriteClock clock = new() { ObserveClose = true };
        await using Harness harness = new(clock);
        FakeSocket logs = harness.Connect();
        await logs.Request("eth_subscribe", "logs");
        logs.StallAfter = 1;
        FakeSocket heads = harness.Connect();
        await heads.Request("eth_subscribe", "newHeads");
        try
        {
            Block busy = harness.Source.Add(11, harness.Source.Current, 1, logCount: 250);
            harness.Publish(busy);
            await logs.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertHead(await heads.Next(), busy);
            Block parent = busy;
            for (ulong number = 12; number <= 20; number++)
            {
                Block next = harness.Source.Add(number, parent, 2);
                harness.Publish(next);
                AssertHead(await heads.Next(), next);
                if (number < 20)
                {
                    logs.ResumeSend();
                    AssertLog(await logs.Next(), busy.Hash!, false);
                }
                parent = next;
            }

            await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            logs.ResumeSend();
            await logs.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Tasks[0].WaitAsync(TimeSpan.FromSeconds(5));
            Block last = harness.Source.Add(21, parent, 3);
            harness.Publish(last);
            AssertHead(await heads.Next(), last);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(logs.CloseStatus, Is.EqualTo((WebSocketCloseStatus)1013));
                Assert.That(logs.Aborted.Task.IsCompleted, Is.False);
                Assert.That(logs.Disposed, Is.True);
                Assert.That(heads.State, Is.EqualTo(WebSocketState.Open));
            }
        }
        finally
        {
            logs.StallAfter = int.MaxValue;
            logs.ResumeSend();
        }
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

        JsonElement invalid = await socket.Request("eth_unsubscribe", id, false);
        Assert.That(invalid.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
        JsonElement unknown = await socket.Request("eth_unsubscribe", "0xunknown");
        Assert.That(unknown.GetProperty("result").GetBoolean(), Is.False);
        JsonElement removed = await socket.Request("eth_unsubscribe", id);
        Assert.That(removed.GetProperty("result").GetBoolean(), Is.True);
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
    public async Task An_empty_websocket_message_returns_a_parse_error_and_keeps_the_connection_open()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        socket.PushRaw([]);

        JsonElement error = await socket.Next();
        JsonElement response = await socket.Request("eth_chainId");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32700));
            Assert.That(error.GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }
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
    public async Task An_oversized_batch_returns_a_limit_error_without_closing_the_connection()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        harness.Source.Code = new byte[100_000];
        int calls = 0;
        harness.Source.BeforeCodeResponse = count => calls = count;
        socket.Push(Enumerable.Repeat(Request("eth_getCode", Address.ToString(), "latest"), 32).ToArray());

        JsonElement response = await socket.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(response[0].GetProperty("result").GetString()!.Length, Is.EqualTo(200_002));
            Assert.That(response[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }
        Assert.That((await socket.Request("eth_chainId")).GetProperty("result").GetString(), Is.EqualTo("0x1"));
    }

    [Test]
    public async Task Batch_limit_errors_preserve_subscription_acknowledgements()
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        HashSet<string> acknowledged = [(await socket.Request("eth_subscribe", "newHeads")).GetProperty("result").GetString()!];
        harness.Source.Code = new byte[150_000];
        socket.Push(new[] { Request("eth_subscribe", "newHeads"), Request("eth_getCode", Address.ToString(), "latest") });

        JsonElement failed = await socket.Next();
        acknowledged.Add(failed[0].GetProperty("result").GetString()!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(failed[1].GetProperty("id").GetInt32(), Is.EqualTo(1));
        }
        for (int i = 2; i < RpcSubscriptions.MaxSubscriptions; i++)
            acknowledged.Add((await socket.Request("eth_subscribe", "newHeads")).GetProperty("result").GetString()!);

        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        harness.Publish(next);
        int notifications = acknowledged.Count;
        for (int i = 0; i < notifications; i++)
            Assert.That(acknowledged.Remove((await socket.Next()).GetProperty("params").GetProperty("subscription").GetString()!), Is.True);
        bool unexpectedNotification = socket.Output.Reader.TryRead(out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acknowledged, Is.Empty);
            Assert.That(unexpectedNotification, Is.False);
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }
    }

    [Test]
    public async Task Notification_subscriptions_activate_with_their_completed_frame([Values] bool mixedBatch)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        object notification = new { jsonrpc = "2.0", method = "eth_subscribe", @params = new[] { "newHeads" } };
        string? acknowledged = null;
        if (mixedBatch)
        {
            socket.Push(new[] { notification, Request("eth_subscribe", "newHeads") });
            JsonElement response = await socket.Next();
            Assert.That(response.GetArrayLength(), Is.EqualTo(1));
            acknowledged = response[0].GetProperty("result").GetString();
        }
        else socket.Push(notification);
        Assert.That((await socket.Request("eth_chainId")).GetProperty("result").GetString(), Is.EqualTo("0x1"));

        harness.Publish(harness.Source.Add(11, harness.Source.Current, 1));
        HashSet<string> subscriptions = [];
        for (int i = 0; i < (mixedBatch ? 2 : 1); i++)
            subscriptions.Add((await socket.Next()).GetProperty("params").GetProperty("subscription").GetString()!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(subscriptions, Has.Count.EqualTo(mixedBatch ? 2 : 1));
            if (mixedBatch) Assert.That(subscriptions, Does.Contain(acknowledged));
            Assert.That(socket.Output.Reader.TryRead(out _), Is.False);
        }
        foreach (string id in subscriptions)
            Assert.That((await socket.Request("eth_unsubscribe", id)).GetProperty("result").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Subscriptions_after_an_oversized_response_do_not_leak_capacity([Values] bool notification)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        harness.Source.Code = new byte[150_000];
        object subscribe = notification
            ? new { jsonrpc = "2.0", method = "eth_subscribe", @params = new[] { "newHeads" } }
            : Request("eth_subscribe", "newHeads");
        socket.Push(new[] { Request("eth_getCode", Address.ToString(), "latest"), subscribe });

        JsonElement response = await socket.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(response.GetArrayLength(), Is.EqualTo(notification ? 1 : 2));
        }
        for (int i = 0; i < RpcSubscriptions.MaxSubscriptions; i++)
            Assert.That((await socket.Request("eth_subscribe", "newHeads")).TryGetProperty("result", out _), Is.True);
    }

    [Test]
    public async Task Websocket_batch_execution_caps_keep_per_item_ids([Values(32, 33)] int count)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        int calls = 0;
        harness.Source.BeforeCodeResponse = value => calls = value;
        socket.Push(Enumerable.Range(0, count).Select(id => new
        {
            jsonrpc = "2.0",
            id,
            method = "eth_getCode",
            @params = new[] { Address.ToString(), "latest" },
        }).ToArray());

        JsonElement responses = await socket.Next();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(Math.Min(count, RpcEndpoint.MaxBatchCount)));
            Assert.That(responses.GetArrayLength(), Is.EqualTo(count));
        }
        for (int i = 0; i < count; i++)
        {
            Assert.That(responses[i].GetProperty("id").GetInt32(), Is.EqualTo(i));
            if (i < RpcEndpoint.MaxBatchCount) Assert.That(responses[i].GetProperty("result").GetString(), Is.EqualTo("0x"));
            else Assert.That(responses[i].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
        }
    }

    [Test]
    public async Task A_broad_log_client_cannot_close_filtered_clients([Values] bool singleBlock)
    {
        await using Harness harness = new();
        FakeSocket broad = harness.Connect();
        await broad.Request("eth_subscribe", "logs");
        FakeSocket narrow = harness.Connect();
        await narrow.Request("eth_subscribe", "logs", new { address = Address.Zero.ToString() });
        await narrow.Request("eth_subscribe", "newHeads");
        FakeSocket heads = harness.Connect();
        await heads.Request("eth_subscribe", "newHeads");
        Block parent = harness.Source.Current;
        for (ulong number = 11; number < (singleBlock ? 12UL : 51UL); number++)
        {
            Block next = harness.Source.Add(number, parent, 1, logCount: singleBlock ? 10_001 : 1_500);
            harness.Publish(next);
            JsonElement narrowHead = await narrow.Next();
            JsonElement unfilteredHead = await heads.Next();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(narrowHead.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
                Assert.That(unfilteredHead.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
            }
            parent = next;
        }

        await broad.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(broad.CloseStatus, Is.EqualTo((WebSocketCloseStatus)1013));
            Assert.That(narrow.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(heads.State, Is.EqualTo(WebSocketState.Open));
        }
    }

    [Test]
    public async Task Single_response_size_limits_keep_existing_subscriptions([Values(100_000, 150_000)] int codeBytes)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        await using Harness harness = new(metrics: metrics);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        harness.Source.Code = new byte[codeBytes];

        JsonElement response = await socket.Request("eth_getCode", Address.ToString(), "latest");

        if (codeBytes == 100_000) Assert.That(response.GetProperty("result").GetString(), Has.Length.EqualTo(codeBytes * 2 + 2));
        else Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
        LightClientMetricsTests.AssertRpc(await LightClientMetricsTests.ScrapeAsync(metrics), "eth_getCode", codeBytes == 100_000 ? "ok" : "error", 1);
        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        harness.Publish(next);
        JsonElement notification = await socket.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notification.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }
    }

    [Test]
    public async Task A_request_deadline_keeps_existing_subscriptions()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        WriteClock clock = new();
        TaskCompletionSource<VerifiedHead> repair = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = new(clock, token => repair.Task.WaitAsync(token), metrics);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        harness.Source.LatestFresh = false;
        clock.ObservePublication = true;
        Task<JsonElement> pending = socket.Request("eth_getBalance", Address.ToString(), "latest");
        await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Expire();

        JsonElement response = await pending;
        Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32002));
        LightClientMetricsTests.AssertRpc(await LightClientMetricsTests.ScrapeAsync(metrics), "eth_getBalance", "cancelled", 1);
        harness.Source.LatestFresh = true;
        Block next = harness.Source.Add(11, harness.Source.Current, 1);
        harness.Publish(next);
        JsonElement notification = await socket.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notification.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }
    }

    [Test]
    public async Task Unmatched_logs_do_not_exhaust_history_or_close_head_subscribers()
    {
        await using Harness harness = new();
        FakeSocket logs = harness.Connect();
        await logs.Request("eth_subscribe", "logs", new { address = Address.Zero.ToString() });
        await logs.Request("eth_subscribe", "newHeads");
        FakeSocket heads = harness.Connect();
        await heads.Request("eth_subscribe", "newHeads");
        Block parent = harness.Source.Current;
        for (ulong number = 11; number <= 50; number++)
        {
            Block next = harness.Source.Add(number, parent, 1, logCount: 1_500);
            harness.Publish(next);
            AssertHead(await logs.Next(), next);
            Assert.That((await heads.Next()).GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
            parent = next;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heads.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(logs.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(harness.Source.BlockReads, Is.Zero, "unmatched verified receipts do not require a body");
        }
    }

    [Test]
    public async Task Invalid_log_data_closes_only_clients_that_requested_logs()
    {
        await using Harness harness = new();
        FakeSocket logs = harness.Connect();
        await logs.Request("eth_subscribe", "logs");
        FakeSocket heads = harness.Connect();
        await heads.Request("eth_subscribe", "newHeads");
        harness.Source.TamperReceipts = true;
        Block next = harness.Source.Add(11, harness.Source.Current, 1);

        harness.Publish(next);

        await logs.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement notification = await heads.Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notification.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(next.Hash!.ToString()));
            Assert.That(heads.State, Is.EqualTo(WebSocketState.Open));
        }
    }

    [TestCase("syncing")]
    public async Task Unsupported_subscription_types_are_rejected(string type)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        Assert.That((await socket.Request("eth_subscribe", type)).GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
    }

    [Test]
    public async Task Pending_subscription_reports_only_local_admissions_in_the_requested_shape([Values] bool full)
    {
        await using Harness harness = new();
        FakeSocket socket = harness.Connect();
        string id = (await socket.Request("eth_subscribe", "newPendingTransactions", full)).GetProperty("result").GetString()!;
        string filterId = (await harness.HttpRequest("eth_newPendingTransactionFilter", [])).GetProperty("result").GetString()!;
        Transaction transaction = new() { SenderAddress = Address, To = Address.Zero, GasLimit = 21_000, Hash = Keccak.Compute("local pending") };

        harness.Subscriptions.PublishPendingTransaction(transaction);

        JsonElement parameters = (await socket.Next()).GetProperty("params");
        JsonElement pending = parameters.GetProperty("result");
        JsonElement changes = (await harness.HttpRequest("eth_getFilterChanges", [filterId])).GetProperty("result");
        JsonElement consumed = (await harness.HttpRequest("eth_getFilterChanges", [filterId])).GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.GetProperty("subscription").GetString(), Is.EqualTo(id));
            Assert.That((full ? pending.GetProperty("hash") : pending).GetString(), Is.EqualTo(transaction.Hash.ToString()));
            if (full) Assert.That(pending.GetProperty("blockHash").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(changes.EnumerateArray().Select(hash => hash.GetString()), Is.EqualTo(new[] { transaction.Hash.ToString() }));
            Assert.That(consumed.GetArrayLength(), Is.Zero);
        }
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
        for (int i = 0; i < RpcSubscriptions.MaxSubscriptions; i++)
            Assert.That((await first.Request("eth_subscribe", "newHeads")).TryGetProperty("result", out _), Is.True);
        Assert.That((await first.Request("eth_subscribe", "newHeads")).GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32000));
        for (int i = 1; i < RpcSubscriptions.MaxClients; i++)
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
        WriteClock clock = new() { ObserveClose = true };
        await using Harness harness = new(clock);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_chainId");
        socket.StallAfter = 1;
        harness.Source.Code = new byte[100_000];
        harness.Source.BeforeCodeResponse = count => { if (count == 7) clock.ObserveWrites = true; };
        for (int i = 0; i < 16; i++) socket.Push(Request("eth_getCode", Address.ToString(), "latest"));

        await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Expire();
        await clock.CloseCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Expire();
        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(socket.Sends, Is.EqualTo(2), "one successful reply and one stalled reply, followed by a full queue that misses its deadline");
    }

    [Test]
    public async Task A_stalled_send_is_bounded_even_when_the_queue_is_not_full()
    {
        WriteClock clock = new();
        await using Harness harness = new(clock);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "newHeads");
        socket.StallAfter = socket.Sends;
        clock.ObserveWrites = true;

        harness.Publish(harness.Source.Add(11, harness.Source.Current, 1));
        await socket.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Expire();

        await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(socket.Sends, Is.EqualTo(2), "the single notification send must time out without needing more queued messages");
    }

    [Test]
    public async Task Request_abort_cancels_shared_dispatch_and_disposes_only_its_socket()
    {
        await using Harness harness = new();
        using CancellationTokenSource cancellation = new();
        FakeSocket aborted = harness.Connect(cancellation.Token);
        FakeSocket healthy = harness.Connect();
        harness.Source.BlockGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        aborted.Push(Request("eth_getBlockByHash", harness.Source.Current.Hash!.ToString(), false));
        await harness.Source.FirstBodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        await harness.Tasks[0].WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement response = await healthy.Request("eth_chainId");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aborted.Aborted.Task.IsCompleted, Is.True);
            Assert.That(aborted.Disposed, Is.True);
            Assert.That(harness.Source.ActiveBlockReads, Is.Zero);
            Assert.That(healthy.State, Is.EqualTo(WebSocketState.Open));
            Assert.That(response.GetProperty("result").GetString(), Is.EqualTo("0x1"));
        }
    }

    [Test]
    public async Task Shutdown_joins_connections_and_disposes_their_sockets(
        [Values("idle", "send", "receipts", "close")] string blocked)
    {
        WriteClock clock = new() { ObserveClose = blocked == "close" };
        Harness harness = new(clock);
        FakeSocket socket = harness.Connect();
        await socket.Request("eth_subscribe", "logs");
        FakeSocket heads = harness.Connect();
        await heads.Request("eth_subscribe", "newHeads");
        if (blocked is "send" or "receipts")
        {
            if (blocked == "send") socket.StallAfter = 1;
            else harness.Source.BlockGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Block next = harness.Source.Add(11, harness.Source.Current, 1, logCount: 250);
            harness.Publish(next);
            if (blocked == "send") await socket.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else await harness.Source.FirstBodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertHead(await heads.Next(), next);
        }
        socket.StallClose = blocked == "close";

        Task stopped = harness.DisposeAsync().AsTask();
        if (blocked == "send") socket.ResumeSend();
        await socket.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await heads.CloseFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (blocked == "close")
        {
            await harness.Tasks[1].WaitAsync(TimeSpan.FromSeconds(5));
            await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Expire();
        }
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(socket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            Assert.That(heads.CloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            Assert.That(socket.Aborted.Task.IsCompleted, Is.EqualTo(blocked == "close"));
            Assert.That(heads.Aborted.Task.IsCompleted, Is.False);
            Assert.That(socket.Disposed, Is.True);
            Assert.That(heads.Disposed, Is.True);
            Assert.That(harness.Source.ActiveBlockReads, Is.Zero);
            Assert.That(harness.Tasks, Has.All.Property(nameof(Task.IsCompleted)).True);
            Assert.That(harness.Contexts.Select(context => context.Features.Get<IHttpRequestLifetimeFeature>())
                .Cast<RequestLifetimeFeature>().Select(feature => feature.Aborted), Is.All.False);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Client_close_receives_a_matching_close_frame(CancellationToken token)
    {
        await using Harness harness = new();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/ws", harness.Subscriptions.HandleAsync);
        await app.StartAsync(token);
        using ClientWebSocket socket = new();
        Uri endpoint = new(app.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");
        await socket.ConnectAsync(endpoint, token);
        const string reason = "wallet disconnected";
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, token);
        WebSocketReceiveResult frame = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(frame.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
            Assert.That(frame.CloseStatusDescription, Is.EqualTo(reason));
        }
    }

    private static object Request(string method, params object[] parameters) => new { jsonrpc = "2.0", id = 1, method, @params = parameters };

    private static void AssertHead(JsonElement notification, Block block) =>
        Assert.That(notification.GetProperty("params").GetProperty("result").GetProperty("hash").GetString(), Is.EqualTo(block.Hash!.ToString()));

    private static void AssertLog(JsonElement notification, Hash256 hash, bool removed, string? expectedData = null)
    {
        JsonElement log = notification.GetProperty("params").GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.GetProperty("blockHash").GetString(), Is.EqualTo(hash.ToString()));
            Assert.That(log.GetProperty("removed").GetBoolean(), Is.EqualTo(removed));
            Assert.That(log.GetProperty("address").GetString(), Is.EqualTo(Address.ToString()));
            Assert.That(log.GetProperty("topics")[0].GetString(), Is.EqualTo(Topic.ToString()));
            if (expectedData is not null) Assert.That(log.GetProperty("data").GetString(), Is.EqualTo(expectedData));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal Source Source { get; } = new();
        private readonly RpcBackend _rpc;
        private readonly RpcService _service;
        internal RpcSubscriptions Subscriptions { get; }
        internal List<Task> Tasks { get; } = [];
        internal List<HttpContext> Contexts { get; } = [];

        internal Harness(TimeProvider? clock = null, Func<CancellationToken, Task<VerifiedHead>>? waitForLatest = null, LightClientMetrics? metrics = null)
        {
            _rpc = new(Source, () => Head(Source.Current), 1, specProvider: MainnetSpecProvider.Instance, getLatestHead: () => Head(Source.Current),
                isLatestHeadFresh: _ => Source.LatestFresh, waitForLatestHead: waitForLatest);
            _service = new(_rpc, Nethermind.Logging.NullLogManager.Instance);
            Nethermind.JsonRpc.IJsonRpcProcessor processor = _service.CreateProcessor(new RpcServiceDiagnostics(_service.Service, NullLogger.Instance, metrics));
            Subscriptions = new(Source, _rpc, processor, NullLogger.Instance, clock);
            Subscriptions.PublishHead(Head(Source.Current));
        }

        internal FakeSocket Connect(CancellationToken requestAborted = default)
        {
            FakeSocket socket = new();
            DefaultHttpContext context = new() { RequestAborted = requestAborted };
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            context.Features.Set<IHttpRequestLifetimeFeature>(new RequestLifetimeFeature { RequestAborted = requestAborted });
            Contexts.Add(context);
            Tasks.Add(Subscriptions.HandleAsync(context));
            return socket;
        }

        internal void Publish(Block block)
        {
            Source.Current = block;
            Subscriptions.PublishHead(Head(block));
        }

        internal async Task<JsonElement> HttpRequest(string method, object[] parameters, CancellationToken token = default)
        {
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(Request(method, parameters));
            using MemoryStream input = new(request);
            using MemoryStream output = new();
            DefaultHttpContext context = new() { RequestAborted = token };
            context.Request.Body = input;
            context.Request.ContentLength = request.Length;
            context.Response.Body = output;
            await RpcEndpoint.HandleAsync(context, _service.Processor, NullLogger.Instance);
            using JsonDocument response = JsonDocument.Parse(output.ToArray());
            return response.RootElement.Clone();
        }

        internal Task StopAsync() => Subscriptions.DisposeAsync().AsTask();

        public async ValueTask DisposeAsync()
        {
            await Subscriptions.DisposeAsync();
            await Task.WhenAll(Tasks);
            _service.Dispose();
            _rpc.Dispose();
            Source.Dispose();
        }

        internal static VerifiedHead Head(Block block) => new(100, block.Number, block.Hash!, block.StateRoot!);
    }

    private sealed class ExecutionLoad : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Source _source;
        private readonly RpcBackend _rpc;
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
        internal bool ObserveClose { get; set; }
        internal TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly List<Timer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(callback, state);
            if (ObserveWrites && dueTime == TimeSpan.FromSeconds(10) || ObservePublication && dueTime == TimeSpan.FromSeconds(90)
                || ObserveClose && dueTime == TimeSpan.FromSeconds(2))
            {
                lock (_lock) _timers.Add(timer);
                Created.TrySetResult();
                if (dueTime == TimeSpan.FromSeconds(2)) CloseCreated.TrySetResult();
            }
            return timer;
        }

        internal void Expire()
        {
            Timer[] timers;
            lock (_lock) timers = [.. _timers];
            foreach (Timer timer in timers) timer.Expire();
        }

        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal void Expire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class RequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
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
        internal TaskCompletionSource CloseFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<bool> _resume = Channel.CreateUnbounded<bool>();
        private byte[]? _pending;
        private int _offset;
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;
        internal bool Accepted { get; set; }
        internal bool Disposed { get; private set; }
        internal int StallAfter { get; set; } = int.MaxValue;
        internal bool StallClose { get; set; }
        internal int Sends { get; private set; }
        internal WebSocketMessageType MessageType { get; set; } = WebSocketMessageType.Text;
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        internal void Push(object request) => PushRaw(JsonSerializer.SerializeToUtf8Bytes(request));
        internal void PushRaw(byte[] message) => _input.Writer.TryWrite(message);
        internal void ResumeSend() => _resume.Writer.TryWrite(true);
        internal async Task<JsonElement> Request(string method, params object[] parameters)
        {
            Push(RpcSubscriptionsTests.Request(method, parameters));
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
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            CloseFrame.TrySetResult();
            if (!StallClose) _input.Writer.TryWrite([]);
            return Task.CompletedTask;
        }
        public override void Dispose()
        {
            Disposed = true;
            if (_state != WebSocketState.Aborted) _state = WebSocketState.Closed;
            _input.Writer.TryComplete();
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            try { _pending ??= await _input.Reader.ReadAsync(cancellationToken); }
            catch (OperationCanceledException) { Abort(); throw; }
            catch (ChannelClosedException) { throw new WebSocketException("Closed test socket"); }
            if (_pending.Length == 0 && _closeStatus is not null)
            {
                _pending = null;
                _state = WebSocketState.Closed;
                return new(0, WebSocketMessageType.Close, true);
            }
            int count = Math.Min(buffer.Count, _pending.Length - _offset);
            _pending.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            bool end = _offset == _pending.Length;
            if (end) { _pending = null; _offset = 0; }
            return new(count, MessageType, end);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            try
            {
                if (++Sends > StallAfter)
                {
                    Stalled.TrySetResult();
                    await _resume.Reader.ReadAsync(cancellationToken);
                }
                await Output.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
            }
            catch (OperationCanceledException) { Abort(); throw; }
        }
    }

    private sealed class Source : IExecutionStateSource, IDisposable
    {
        private readonly PrivateKey _key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        private readonly ConcurrentDictionary<Hash256, (Block Block, TxReceipt[] Receipts)> _blocks = [];
        internal Block Current { get; set; }
        internal bool TamperHeader { get; set; }
        internal bool TamperReceipts { get; set; }
        internal byte[] Code { get; set; } = [];
        internal Action<int>? BeforeCodeResponse { get; set; }
        internal Action<BlockHeader>? HeaderRequested { get; set; }
        internal bool RejectBlocks { get; set; }
        internal bool LatestFresh { get; set; } = true;
        internal TaskCompletionSource? BlockGate { get; set; }
        internal TaskCompletionSource FirstBodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BlocksStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HeaderReads => Volatile.Read(ref _headerReads);
        internal int BlockReads => Volatile.Read(ref _blockReads);
        internal int ReceiptReads => Volatile.Read(ref _receiptReads);
        internal int ActiveBlockReads => Volatile.Read(ref _activeBlockReads);
        private int _codeCalls;
        private int _headerReads;
        private int _blockReads;
        private int _receiptReads;
        private int _blockedBlockReads;
        private int _activeBlockReads;

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
            header.Hash = Keccak.Compute(Rlp.Encode(header));
            Block block = new(header, new BlockBody([transaction], [], []));
            Assert.That(_blocks.TryAdd(header.Hash, (block, [receipt])), Is.True);
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
            Interlocked.Increment(ref _activeBlockReads);
            try
            {
                if (RejectBlocks) throw new InvalidOperationException("A header subscription must not request a block body.");
                if (BlockGate is { } gate)
                {
                    FirstBodyStarted.TrySetResult();
                    if (Interlocked.Increment(ref _blockedBlockReads) == 4) BlocksStarted.TrySetResult();
                    await gate.Task.WaitAsync(cancellationToken);
                }
                Block block = _blocks[header.Hash!].Block;
                ExecutionPeerTransport.VerifyBlockBody(header, block.Body);
                return block;
            }
            finally { Interlocked.Decrement(ref _activeBlockReads); }
        }
        public Task<TxReceipt[]> GetReceiptsAsync(BlockHeader header, CancellationToken cancellationToken, int? transactionCount = null)
        {
            Interlocked.Increment(ref _receiptReads);
            TxReceipt[] receipts = _blocks[header.Hash!].Receipts;
            if (TamperReceipts) receipts[0].Logs![0].Data[0] ^= 1;
            ExecutionPeerTransport.VerifyReceipts(header, receipts, MainnetSpecProvider.Instance);
            return Task.FromResult(receipts);
        }
        public void Dispose() => _key.Dispose();
    }
}
