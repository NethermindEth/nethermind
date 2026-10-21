// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Evm;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using LogsFilter = Nethermind.JsonRpc.Modules.Eth.Filter;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class ReceiptsEventPayloadCacheTests
{
    private const int LogsPerReceipt = 2;
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private static IEnumerable<string?[]> SubscriptionsThatCannotShare()
    {
        yield return ["a"];
        yield return [null];
        yield return ["a", "b"];
        yield return ["a", "b", "c"];
    }

    [TestCaseSource(nameof(SubscriptionsThatCannotShare))]
    public void ForEvent_without_a_possible_reuse_gives_no_shared_payloads(string?[] filterKeys)
    {
        ReceiptsEventPayloadCache cache = new(static _ => 1);
        ReceiptsEventArgs e = CreateEvent(removed: false, logIndexPerReceipt: false);
        ReceiptsEventPayloadCache.Membership[] members = [.. filterKeys.Select(cache.Join)];

        foreach (ReceiptsEventPayloadCache.Membership member in members)
        {
            EventPayloads payloads = member.ForEvent(e);
            Assert.That(payloads.Stores || payloads.TryGet(0, out _), Is.False);
        }
    }

    [TestCase("a", "a", true, TestName = "Same filter")]
    [TestCase(null, null, true, TestName = "Both unfiltered")]
    [TestCase("a", null, false, TestName = "Filtered and unfiltered")]
    public void ForEvent_with_a_possible_reuse_shares_one_payload(string? storingKey, string? otherKey, bool otherStores)
    {
        ReceiptsEventPayloadCache cache = new(static _ => 1);
        ReceiptsEventArgs e = CreateEvent(removed: false, logIndexPerReceipt: false);
        using ReceiptsEventPayloadCache.Membership storing = cache.Join(storingKey);
        using ReceiptsEventPayloadCache.Membership other = cache.Join(otherKey);

        EventPayloads stored = storing.ForEvent(e);
        Assert.That(stored.Stores, Is.True);
        SerializedJson payload = stored.Store(0, "payload");

        EventPayloads read = other.ForEvent(e);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Stores, Is.EqualTo(otherStores));
            Assert.That(read.TryGet(0, out SerializedJson shared), Is.True);
            Assert.That(shared.Utf8Json, Is.SameAs(payload.Utf8Json));
        }
    }

    [Test]
    public void ForEvent_after_the_other_subscription_with_the_same_filter_left_gives_no_shared_payloads()
    {
        ReceiptsEventPayloadCache cache = new(static _ => 1);
        ReceiptsEventArgs e = CreateEvent(removed: false, logIndexPerReceipt: false);
        using ReceiptsEventPayloadCache.Membership staying = cache.Join("a");
        ReceiptsEventPayloadCache.Membership leaving = cache.Join("a");

        leaving.Dispose();
        leaving.Dispose();

        Assert.That(staying.ForEvent(e).Stores, Is.False);
    }

    [Test]
    public void Store_for_the_same_payload_at_once_keeps_one()
    {
        const int subscribers = 8;
        ReceiptsEventPayloadCache cache = new(static _ => 1);
        ReceiptsEventArgs e = CreateEvent(removed: false, logIndexPerReceipt: false);
        using ReceiptsEventPayloadCache.Membership first = cache.Join("a");
        using ReceiptsEventPayloadCache.Membership second = cache.Join("a");
        using Barrier start = new(subscribers);

        Task<byte[]>[] stores = [.. Enumerable.Range(0, subscribers)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                return first.ForEvent(e).Store(0, "payload").Utf8Json;
            }, TaskCreationOptions.LongRunning))];
        Task.WaitAll(stores);

        Assert.That(stores.Select(static store => store.Result), Is.All.SameAs(stores[0].Result));
    }

    [Test]
    public void Logs_subscribers_of_one_factory_each_receive_their_own_logs_as_a_lone_subscriber_would([Values] bool removed, [Values] bool logIndexPerReceipt)
    {
        ReceiptsEventArgs e = CreateEvent(removed, logIndexPerReceipt);
        using Fixture fixture = new();

        // Two subscribers share address A's logs and the unfiltered one overlaps all of them.
        Address?[] filters = [TestItem.AddressA, TestItem.AddressA, null, TestItem.AddressB];
        (CollectingClient Client, Subscription Subscription)[] subscribers = [.. filters.Select(address =>
            fixture.SubscribeToLogs(address, ExpectedLogs(e, address).Count))];

        fixture.Monitor.Raise(e);

        for (int i = 0; i < filters.Length; i++)
        {
            (CollectingClient client, Subscription subscription) = subscribers[i];
            Assert.That(client.Received.Wait(DeliveryTimeout), Is.True, "every log the filter accepts is published");
            Assert.That(client.Messages, Is.EqualTo(ExpectedLogMessages(e, subscription.Id, filters[i])), "byte for byte what a subscriber serializing its own logs sends");
        }
    }

    [Test]
    public void Receipts_subscribers_of_one_factory_each_receive_their_own_receipts_as_a_lone_subscriber_would([Values] bool logIndexPerReceipt)
    {
        ReceiptsEventArgs e = CreateEvent(removed: false, logIndexPerReceipt);
        using Fixture fixture = new();

        // Two unfiltered subscribers share every receipt; the filtered ones overlap them.
        Hash256[]?[] filters = [null, null, [TestItem.KeccakD], [TestItem.KeccakD, TestItem.KeccakC], []];
        (CollectingClient Client, Subscription Subscription)[] subscribers = [.. filters.Select(hashes =>
            fixture.SubscribeToReceipts(hashes, ExpectedReceipts(e, hashes).Count))];

        fixture.Monitor.Raise(e);

        for (int i = 0; i < filters.Length; i++)
        {
            (CollectingClient client, Subscription subscription) = subscribers[i];
            if (client.ExpectedMessages > 0) Assert.That(client.Received.Wait(DeliveryTimeout), Is.True, "every receipt the filter accepts is published");
            Assert.That(client.Messages, Is.EqualTo(ExpectedReceiptMessages(e, subscription.Id, filters[i])), "byte for byte what a subscriber serializing its own receipts sends");
        }
    }

    [TestCase(true, false, true, TestName = "Logs: same address")]
    [TestCase(true, true, true, TestName = "Logs: same addresses in another order")]
    [TestCase(true, false, false, TestName = "Logs: different addresses")]
    [TestCase(false, false, true, TestName = "Receipts: same hashes")]
    [TestCase(false, true, true, TestName = "Receipts: same hashes in another order")]
    [TestCase(false, false, false, TestName = "Receipts: different hashes")]
    public void Subscribers_share_payloads_only_when_their_filters_match(bool logs, bool reordered, bool sameFilter)
    {
        ReceiptsEventArgs e = CreateEvent(removed: false, logIndexPerReceipt: false);
        using Fixture fixture = new();
        // Address A and C's logs are at positions 0, 2 and 3; transactions C and D are both receipts.
        int expected = logs ? 3 : 2;
        (CollectingClient client, _) = logs
            ? fixture.SubscribeToLogs([TestItem.AddressA, TestItem.AddressC], expected)
            : fixture.SubscribeToReceipts([TestItem.KeccakC, TestItem.KeccakD], expected);
        Address[] otherAddresses = sameFilter ? (reordered ? [TestItem.AddressC, TestItem.AddressA] : [TestItem.AddressA, TestItem.AddressC]) : [TestItem.AddressB];
        Hash256[] otherHashes = sameFilter ? (reordered ? [TestItem.KeccakD, TestItem.KeccakC] : [TestItem.KeccakC, TestItem.KeccakD]) : [TestItem.KeccakE];
        (CollectingClient other, _) = logs
            ? fixture.SubscribeToLogs(otherAddresses, sameFilter ? expected : 1)
            : fixture.SubscribeToReceipts(otherHashes, sameFilter ? expected : 0);

        fixture.Monitor.Raise(e);
        Assert.That(client.Received.Wait(DeliveryTimeout), Is.True);
        if (other.ExpectedMessages > 0) Assert.That(other.Received.Wait(DeliveryTimeout), Is.True);

        // An unfiltered subscriber joining now only reads what the others stored for the event.
        using ReceiptsEventPayloadCache.Membership probe = (logs ? fixture.LogPayloads : fixture.ReceiptPayloads).Join(null);
        EventPayloads payloads = probe.ForEvent(e);
        Assert.That(payloads.TryGet(0, out _), Is.EqualTo(sameFilter));
    }

    [Test]
    public void Subscribing_and_unsubscribing_while_events_are_delivered_keeps_every_message_intact()
    {
        const int events = 50;
        ReceiptsEventArgs[] published = [.. Enumerable.Range(0, events).Select(static i => CreateEvent(removed: i % 2 == 1, logIndexPerReceipt: false))];
        using Fixture fixture = new();
        int expectedPerEvent = ExpectedLogs(published[0], TestItem.AddressA).Count;
        (CollectingClient Client, Subscription Subscription)[] subscribers =
        [
            fixture.SubscribeToLogs(TestItem.AddressA, events * expectedPerEvent),
            fixture.SubscribeToLogs(TestItem.AddressA, events * expectedPerEvent),
        ];

        using CancellationTokenSource stop = new();
        Task churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                (_, Subscription same) = fixture.SubscribeToLogs(TestItem.AddressA, int.MaxValue);
                (_, Subscription unfiltered) = fixture.SubscribeToLogs((Address?)null, int.MaxValue);
                same.Dispose();
                unfiltered.Dispose();
            }
        });

        foreach (ReceiptsEventArgs e in published) fixture.Monitor.Raise(e);
        foreach ((CollectingClient client, _) in subscribers) Assert.That(client.Received.Wait(DeliveryTimeout), Is.True);
        stop.Cancel();
        churn.Wait();

        foreach ((CollectingClient client, Subscription subscription) in subscribers)
        {
            Assert.That(client.Messages, Is.EqualTo(published.SelectMany(e => ExpectedLogMessages(e, subscription.Id, TestItem.AddressA))));
        }
    }

    /// <summary>Two receipts of <see cref="LogsPerReceipt"/> logs: positions 0 and 2 come from address A, 1 from B and 3 from C.</summary>
    private static ReceiptsEventArgs CreateEvent(bool removed, bool logIndexPerReceipt)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(7).TestObject;
        LogEntry fromA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
        LogEntry fromB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).TestObject;
        LogEntry fromC = Build.A.LogEntry.WithAddress(TestItem.AddressC).WithData(TestItem.RandomDataC).TestObject;
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithBlockNumber(header.Number).WithBlockHash(header.Hash).WithIndex(0).WithTransactionHash(TestItem.KeccakC).WithLogs(fromA, fromB).TestObject,
            Build.A.Receipt.WithBlockNumber(header.Number).WithBlockHash(header.Hash).WithIndex(1).WithTransactionHash(TestItem.KeccakD).WithLogs(fromA, fromC).TestObject,
        ];
        header.Bloom = new Bloom(new[] { fromA, fromB, fromC });
        return new ReceiptsEventArgs(header, receipts, removed, logIndexPerReceipt);
    }

    private static List<FilterLog> ExpectedLogs(ReceiptsEventArgs e, Address? address)
    {
        List<FilterLog> logs = [];
        for (int position = 0; position < e.TxReceipts.Length * LogsPerReceipt; position++)
        {
            TxReceipt receipt = e.TxReceipts[position / LogsPerReceipt];
            int inReceipt = position % LogsPerReceipt;
            if (address is not null && receipt.Logs![inReceipt].Address != address) continue;

            logs.Add(new FilterLog(e.LogIndexPerReceipt ? inReceipt : position, receipt, receipt.Logs![inReceipt], e.BlockHeader.Timestamp, e.WasRemoved));
        }

        return logs;
    }

    private static List<ReceiptForRpc> ExpectedReceipts(ReceiptsEventArgs e, Hash256[]? hashes) =>
        [.. e.TxReceipts
            .Select((receipt, i) => (receipt, i))
            .Where(r => hashes is null || hashes.Contains(r.receipt.TxHash!))
            .Select(r => new ReceiptForRpc(r.receipt.TxHash!, r.receipt, e.BlockHeader.Timestamp, new TxGasInfo(), e.LogIndexPerReceipt ? 0 : r.i * LogsPerReceipt))];

    private static IEnumerable<string> ExpectedLogMessages(ReceiptsEventArgs e, string subscriptionId, Address? address) =>
        ExpectedLogs(e, address).Select(log => Message(log, subscriptionId));

    private static IEnumerable<string> ExpectedReceiptMessages(ReceiptsEventArgs e, string subscriptionId, Hash256[]? hashes) =>
        ExpectedReceipts(e, hashes).Select(receipt => Message(receipt, subscriptionId));

    private static string Message<T>(T result, string subscriptionId) => RpcTest.SerializeResponse(new JsonRpcSubscriptionResponse<T>
    {
        Params = new JsonRpcSubscriptionResult<T> { Result = result, Subscription = subscriptionId },
        MethodName = SubscriptionMethodName.EthSubscription
    });

    /// <summary>A factory's logs and receipts subscriptions, created as the factory does but with their caches at hand.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly FilterStore _filterStore = new(new TimerFactory());
        private readonly IBlockTree _blockTree = Substitute.For<IBlockTree>();
        private readonly List<Subscription> _subscriptions = [];

        public ReceiptMonitor Monitor { get; } = new();
        public ReceiptsEventPayloadCache LogPayloads { get; } = LogsSubscription.CreatePayloadCache();
        public ReceiptsEventPayloadCache ReceiptPayloads { get; } = TransactionReceiptsSubscription.CreatePayloadCache();

        public (CollectingClient, Subscription) SubscribeToLogs(Address? address, int expectedMessages) =>
            SubscribeToLogs(address is null ? null : [address], expectedMessages);

        public (CollectingClient, Subscription) SubscribeToLogs(Address[]? addresses, int expectedMessages)
        {
            CollectingClient client = new(expectedMessages);
            LogsFilter? filter = addresses is null ? null : new() { Address = [.. addresses.Select(static a => new AddressAsKey(a))] };
            return Track(client, new LogsSubscription(client, Monitor, _filterStore, _blockTree, NullLogManager.Instance, LogPayloads, filter));
        }

        public (CollectingClient, Subscription) SubscribeToReceipts(Hash256[]? hashes, int expectedMessages)
        {
            CollectingClient client = new(expectedMessages);
            TransactionHashesFilter? filter = hashes is null ? null : new() { TransactionHashes = [.. hashes.Select(static h => h.ValueHash256)] };
            return Track(client, new TransactionReceiptsSubscription(client, Monitor, _blockTree, NullLogManager.Instance, ReceiptPayloads, filter));
        }

        private (CollectingClient, Subscription) Track(CollectingClient client, Subscription subscription)
        {
            lock (_subscriptions) _subscriptions.Add(subscription);
            return (client, subscription);
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (Subscription subscription in _subscriptions) subscription.Dispose();
            }

            _filterStore.Dispose();
        }
    }

    private sealed class ReceiptMonitor : IReceiptMonitor
    {
        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public void Raise(ReceiptsEventArgs e) => ReceiptsInserted?.Invoke(this, e);

        public void Dispose() { }
    }

    /// <summary>Serializes each message as it is sent, since the subscription disposes it afterwards.</summary>
    private sealed class CollectingClient(int expectedMessages) : IJsonRpcDuplexClient
    {
        public int ExpectedMessages => expectedMessages;
        public List<string> Messages { get; } = [];
        public ManualResetEventSlim Received { get; } = new();

        public string Id => "test";

        public event EventHandler? Closed { add { } remove { } }

        public Task<int> SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken = default)
        {
            Messages.Add(RpcTest.SerializeResponse(result.Response));
            if (Messages.Count == expectedMessages) Received.Set();
            return Task.FromResult(0);
        }

        public void Dispose() => Received.Dispose();
    }
}
