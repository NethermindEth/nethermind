// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Delivers one block to many <c>logs</c> or <c>transactionReceipts</c> subscribers of one factory, through the send
/// queues and serializing every message as a socket client writes it.
/// </summary>
/// <remarks>
/// With <see cref="FilterMode.Same"/> every logs subscriber watches the same contract and every receipts subscriber is
/// unfiltered; with <see cref="FilterMode.Own"/> each watches its own contract or transaction, so nothing can be shared;
/// <see cref="FilterMode.Mixed"/> adds one unfiltered subscriber to those.
/// </remarks>
public class ReceiptsSubscriptionFanOutBenchmarks
{
    private const int Receipts = 50;
    private const int LogsPerReceipt = 4;
    private const int Logs = Receipts * LogsPerReceipt;

    [Params(SubscriptionType.EthSubscription.Logs, SubscriptionType.EthSubscription.TransactionReceipts)]
    public string Kind { get; set; } = SubscriptionType.EthSubscription.Logs;

    [Params(1, 10, 100, 500)]
    public int Subscribers { get; set; }

    [Params(FilterMode.Same, FilterMode.Own, FilterMode.Mixed)]
    public FilterMode Filters { get; set; }

    public enum FilterMode { Same, Own, Mixed }

    private readonly FakeReceiptMonitor _monitor = new();
    private FilterStore _filterStore = null!;
    private Subscription[] _subscriptions = null!;
    private BlockHeader _header = null!;
    private TxReceipt[] _receipts = null!;
    private int _expectedMessages;
    private int _pending;
    private readonly SemaphoreSlim _delivered = new(0);

    [GlobalSetup]
    public void Setup()
    {
        int addresses = Filters == FilterMode.Same ? 1 : Subscribers;
        Address[] contracts = [.. Enumerable.Range(0, addresses).Select(static i => Address.FromNumber((UInt256)(i + 1)))];

        _header = Build.A.BlockHeader.WithNumber(1).TestObject;
        LogEntry[] allLogs = new LogEntry[Logs];
        _receipts = new TxReceipt[Receipts];
        for (int i = 0; i < Receipts; i++)
        {
            LogEntry[] logs = new LogEntry[LogsPerReceipt];
            for (int j = 0; j < LogsPerReceipt; j++)
            {
                int position = i * LogsPerReceipt + j;
                logs[j] = allLogs[position] = Build.A.LogEntry
                    .WithAddress(contracts[position % addresses])
                    .WithTopics(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC)
                    .WithData(new byte[64])
                    .TestObject;
            }

            _receipts[i] = Build.A.Receipt.WithBlockNumber(_header.Number).WithBlockHash(_header.Hash).WithIndex(i).WithTransactionHash(TransactionHash(i)).WithLogs(logs).TestObject;
        }

        _header.Bloom = new Bloom(allLogs);

        _filterStore = new FilterStore(new TimerFactory());
        SubscriptionFactory factory = new();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        factory.RegisterLogsSubscription(_monitor, _filterStore, blockTree, NullLogManager.Instance);
        factory.RegisterTransactionReceiptsSubscription(_monitor, blockTree, MainnetSpecProvider.Instance, NullLogManager.Instance);

        bool isLogs = Kind == SubscriptionType.EthSubscription.Logs;
        bool same = Filters == FilterMode.Same;
        int unfiltered = Filters == FilterMode.Mixed ? 1 : 0;
        _expectedMessages = (isLogs, same) switch
        {
            (true, true) => Logs * Subscribers,
            (true, false) => Logs * (1 + unfiltered),
            (false, true) => Receipts * Subscribers,
            (false, false) => Math.Min(Receipts, Subscribers) + Receipts * unfiltered,
        };
        _subscriptions = [.. Enumerable.Range(0, Subscribers + unfiltered).Select(i => factory.CreateSubscription(new SerializingClient(this), Kind, (isLogs, same) switch
        {
            _ when i == Subscribers => null,
            (true, _) => $"{{\"address\":\"{contracts[i % addresses]}\"}}",
            (false, true) => null,
            (false, false) => $"{{\"transactionHashes\":[\"{TransactionHash(i)}\"]}}",
        }))];
    }

    private static Hash256 TransactionHash(int index) => Keccak.Compute(BitConverter.GetBytes(index));

    /// <remarks>
    /// Messages are sent on the subscriptions' own threads, which the memory diagnoser does not reliably attribute to the
    /// operation, so the bytes all threads allocate per block are printed here.
    /// </remarks>
    [GlobalCleanup]
    public void Cleanup()
    {
        const int blocks = 200;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < blocks; i++) DeliverBlock();
        Console.WriteLine($"// AllocatedPerBlock {Kind} {Subscribers} {Filters}: {(GC.GetTotalAllocatedBytes(precise: true) - before) / blocks} B");

        foreach (Subscription subscription in _subscriptions) subscription.Dispose();
        _filterStore.Dispose();
    }

    [Benchmark]
    public void DeliverBlock()
    {
        Volatile.Write(ref _pending, _expectedMessages);
        _monitor.Raise(new ReceiptsEventArgs(_header, _receipts));
        _delivered.Wait();
    }

    private void OnSent()
    {
        if (Interlocked.Decrement(ref _pending) == 0) _delivered.Release();
    }

    private sealed class FakeReceiptMonitor : IReceiptMonitor
    {
        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public void Raise(ReceiptsEventArgs e) => ReceiptsInserted?.Invoke(this, e);

        public void Dispose() { }
    }

    private sealed class SerializingClient(ReceiptsSubscriptionFanOutBenchmarks benchmark) : IJsonRpcDuplexClient
    {
        private static readonly Task<int> Sent = Task.FromResult(0);
        private readonly ArrayBufferWriter<byte> _buffer = new(4096);

        public string Id => "bench";

        public event EventHandler? Closed { add { } remove { } }

        public Task<int> SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken = default)
        {
            _buffer.ResetWrittenCount();
            JsonRpcResponseWriter.Write(_buffer, result.Response!, EthereumJsonSerializer.JsonOptions);
            benchmark.OnSent();
            return Sent;
        }

        public void Dispose() { }
    }
}
