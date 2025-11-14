// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring.TransactionPool;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

using DataCompletion = System.Threading.Tasks.TaskCompletionSource<byte[]>;

namespace Nethermind.Runner.Monitoring;

public class DataFeed
{
    public static long StartTime { get; set; }

    private readonly ITxPool _txPool;
    private readonly ISpecProvider _specProvider;
    private readonly IReceiptFinder _receiptFinder;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly BlockDecoder _blockDecoder = new();
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;

    private long _subscribers;

    public DataFeed(
        ITxPool txPool,
        ISpecProvider specProvider,
        IReceiptFinder receiptFinder,
        IBlockTree blockTree,
        ISyncPeerPool syncPeerPool,
        IMainProcessingContext mainProcessingContext,
        ILogManager logManager,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(txPool);
        ArgumentNullException.ThrowIfNull(syncPeerPool);
        ArgumentNullException.ThrowIfNull(receiptFinder);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(syncPeerPool);
        ArgumentNullException.ThrowIfNull(mainProcessingContext?.BlockchainProcessor);

        _lifetime = lifetime;
        _txPool = txPool;
        _specProvider = specProvider;
        _receiptFinder = receiptFinder;
        _syncPeerPool = syncPeerPool;

        _logger = logManager.GetClassLogger();

        mainProcessingContext.BlockchainProcessor.NewProcessingStatistics += OnNewProcessingStatistics;
        blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
        ConsoleHelpers.LineWritten += OnConsoleLineWritten;
        _ = StartTxFlowRefresh();
        _ = SystemStatsRefresh();
        _ = StartPeersRefresh();
    }

    public async Task ProcessingFeedAsync(HttpContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _subscribers);
        try
        {
            await ProcessingFeeds(ctx, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal feed cancellation
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Http request {nameof(DataFeed)} errored", e);
        }
        finally
        {
            Interlocked.Decrement(ref _subscribers);
        }
    }

    private async Task ProcessingFeeds(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await ctx.Response.WriteAsync("event: nodeData\ndata: ", ct);
        await ctx.Response.Body.WriteAsync(GetNodeData(), ct);
        await ctx.Response.WriteAsync("\n\n", ct);

        await ctx.Response.WriteAsync("event: txNodes\ndata: ", ct);
        await ctx.Response.Body.WriteAsync(TxPoolFlow.NodeJson, ct);
        await ctx.Response.WriteAsync("\n\n", ct);

        await ctx.Response.WriteAsync("event: log\ndata: ", ct);
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ConsoleHelpers.GetRecentMessages(), JsonSerializerOptions.Web), ct);
        await ctx.Response.WriteAsync("\n\n", ct);

        var channel = Channel.CreateUnbounded<ChannelEntry>();

        InitializeChannelSubscriptions(channel, ct);

        await foreach (ChannelEntry entry in channel.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"event: {entry.Type}\ndata: ", ct);
            await ctx.Response.Body.WriteAsync(entry.Data, ct);
            await ctx.Response.WriteAsync("\n\n", ct);

            if (channel.Reader.Count == 0)
            {
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
    }

    private enum EntryType
    {
        nodeData,
        txNodes,
        log,
        processed,
        txLinks,
        forkChoice,
        system,
        peers
    }

    private class ChannelEntry
    {
        public EntryType Type { get; set; }
        public byte[] Data { get; set; }
    }
    private static async Task ChannelSubscribe(EntryType type, Func<Task<byte[]>> nextTask, Channel<ChannelEntry> channel, CancellationToken ct)
    {
        Task<byte[]> task = nextTask();

        while (!ct.IsCancellationRequested)
        {
            byte[] data = await task;
            task = nextTask();
            await channel.Writer.WriteAsync(new ChannelEntry { Type = type, Data = data }, ct);
        }
    }

    private void InitializeChannelSubscriptions(Channel<ChannelEntry> channel, CancellationToken ct)
    {
        _ = ChannelSubscribe(EntryType.processed, () => _processing.Task, channel, ct);
        _ = ChannelSubscribe(EntryType.log, () => _log.Task, channel, ct);
        _ = ChannelSubscribe(EntryType.forkChoice, () => _forkChoice.Task, channel, ct);
        _ = ChannelSubscribe(EntryType.txLinks, () => _txFlow.Task, channel, ct);
        _ = ChannelSubscribe(EntryType.system, () => _systemStats.Task, channel, ct);
        _ = ChannelSubscribe(EntryType.peers, () => _peers.Task, channel, ct);
    }

    private static byte[] GetNodeData()
        => JsonSerializer.SerializeToUtf8Bytes(
            new NethermindNodeData(Environment.TickCount64 - StartTime),
            JsonSerializerOptions.Web);

    private DataCompletion _txFlow = new();
    private async Task StartTxFlowRefresh()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            await Task.Delay(millisecondsDelay: 1000);
            // No subscribers, no need to prepare event data
            if (!HaveSubscribers) continue;

            byte[] data = GetTxFlowTask();

            DataCompletion txFlow = _txFlow;
            _txFlow = new DataCompletion();
            txFlow.TrySetResult(data);
        }
    }

    private Environment.ProcessCpuUsage _lastCpuUsage;
    private long _lastTimeStamp;
    private DataCompletion _systemStats = new();
    private async Task SystemStatsRefresh()
    {
        _lastCpuUsage = Environment.CpuUsage;
        _lastTimeStamp = Stopwatch.GetTimestamp();
        while (!_lifetime.IsCancellationRequested)
        {
            byte[] data = await GetStatsTask(delayMs: 1000);
            DataCompletion systemStats = _systemStats;
            _systemStats = new();
            systemStats.TrySetResult(data);
        }
    }

    private async Task<byte[]> GetStatsTask(int delayMs)
    {
        await Task.Delay(delayMs);

        Environment.ProcessCpuUsage cpuUsage = Environment.CpuUsage;
        long timeStamp = Stopwatch.GetTimestamp();

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastTimeStamp, timeStamp);

        SystemStats stats = new()
        {
            UserPercent = (cpuUsage.UserTime - _lastCpuUsage.UserTime).TotalMicroseconds / elapsed.TotalMicroseconds / Environment.ProcessorCount,
            PrivilegedPercent = ((cpuUsage.PrivilegedTime - _lastCpuUsage.PrivilegedTime).TotalMicroseconds / elapsed.TotalMicroseconds) / Environment.ProcessorCount,
            WorkingSet = Environment.WorkingSet,
            Uptime = Environment.TickCount64 - StartTime
        };
        _lastTimeStamp = timeStamp;
        _lastCpuUsage = cpuUsage;

        return JsonSerializer.SerializeToUtf8Bytes(stats, JsonSerializerOptions.Web);
    }

    private DataCompletion _peers = new();
    private async Task StartPeersRefresh()
    {
        _lastCpuUsage = Environment.CpuUsage;
        _lastTimeStamp = Stopwatch.GetTimestamp();
        while (!_lifetime.IsCancellationRequested)
        {
            await Task.Delay(millisecondsDelay: 1000);
            // No subscribers, no need to prepare event data
            if (!HaveSubscribers) continue;

            byte[] data = GetPeersTask();
            DataCompletion peers = _peers;
            _peers = new();
            peers.TrySetResult(data);
        }
    }

    private byte[] GetPeersTask()
    {
        List<PeerForWeb> peers = [.. _syncPeerPool.AllPeers.Select(
            static peer => new PeerForWeb
            {
                Contexts = peer.AllocatedContexts,
                ClientType = peer.PeerClientType,
                Version = peer.SyncPeer.ProtocolVersion,
                Head = peer.HeadNumber
            })];

        return JsonSerializer.SerializeToUtf8Bytes(peers, JsonSerializerOptions.Web);
    }

    private byte[] GetTxFlowTask()
    {
        return JsonSerializer.SerializeToUtf8Bytes(new TxPoolFlow(
                    TxPool.Metrics.PendingTransactionsReceived,
                    TxPool.Metrics.PendingTransactionsNotSupportedTxType,
                    TxPool.Metrics.PendingTransactionsSizeTooLarge,
                    TxPool.Metrics.PendingTransactionsGasLimitTooHigh,
                    TxPool.Metrics.PendingTransactionsTooLowPriorityFee,
                    TxPool.Metrics.PendingTransactionsTooLowFee,
                    TxPool.Metrics.PendingTransactionsMalformed,
                    TxPool.Metrics.PendingTransactionsNullHash,
                    TxPool.Metrics.PendingTransactionsKnown,
                    TxPool.Metrics.PendingTransactionsUnresolvableSender,
                    TxPool.Metrics.PendingTransactionsConflictingTxType,
                    TxPool.Metrics.PendingTransactionsNonceTooFarInFuture,
                    TxPool.Metrics.PendingTransactionsZeroBalance,
                    TxPool.Metrics.PendingTransactionsBalanceBelowValue,
                    TxPool.Metrics.PendingTransactionsTooLowBalance,
                    TxPool.Metrics.PendingTransactionsLowNonce,
                    TxPool.Metrics.PendingTransactionsNonceGap,
                    TxPool.Metrics.PendingTransactionsPassedFiltersButCannotReplace,
                    TxPool.Metrics.PendingTransactionsPassedFiltersButCannotCompeteOnFees,
                    TxPool.Metrics.PendingTransactionsEvicted,
                    TxPool.Metrics.TransactionsSourcedPrivateOrderFlow,
                    TxPool.Metrics.TransactionsSourcedMemPool,
                    TxPool.Metrics.TransactionsReorged
            )
        {
            PooledBlobTx = _txPool.GetPendingBlobTransactionsCount(),
            PooledTx = _txPool.GetPendingTransactionsCount(),
            HashesReceived = TxPool.Metrics.PendingTransactionsHashesReceived
        },
            JsonSerializerOptions.Web);
    }

    private DataCompletion _processing = new();
    private void OnNewProcessingStatistics(object? sender, BlockStatistics stats)
    {
        // No subscribers, no need to prepare event data
        if (!HaveSubscribers) return;

        DataCompletion processing = _processing;
        _processing = new DataCompletion();

        processing.TrySetResult(JsonSerializer.SerializeToUtf8Bytes(stats, JsonSerializerOptions.Web));
    }

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoiceUpdateEventArgs choice)
    {
        // No subscribers, no need to prepare event data
        if (!HaveSubscribers) return;

        Task.Run(() =>
        {
            try
            {
                OnForkChoiceUpdated(choice);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("UI Forkchoice data preparation failed", e);
            }
        });
    }

    private DataCompletion _forkChoice = new();
    private void OnForkChoiceUpdated(IBlockTree.ForkChoiceUpdateEventArgs choice)
    {
        DataCompletion forkChoice = Interlocked.Exchange(ref _forkChoice, new DataCompletion());

        Block head = choice.Head;
        Transaction[] txs = head.Transactions;
        IReleaseSpec spec = _specProvider.GetSpec(head.Header);
        ReceiptForRpc[] receipts = _receiptFinder.Get(head).Select((r, i) => new ReceiptForRpc(txs[i].Hash, r, head.Timestamp, txs[i].GetGasInfo(spec, choice.Head.Header))).ToArray();
        forkChoice.TrySetResult(
            JsonSerializer.SerializeToUtf8Bytes(
                new ForkData
                {
                    Head = new BlockForWeb
                    {
                        ExtraData = head.ExtraData ?? [],
                        GasLimit = head.GasLimit,
                        GasUsed = head.GasUsed,
                        Hash = head.Hash ?? Hash256.Zero,
                        Beneficiary = head.Beneficiary ?? Address.Zero,
                        Number = head.Number,
                        Size = _blockDecoder.GetLength(head, RlpBehaviors.None),
                        Timestamp = head.Timestamp,
                        BaseFeePerGas = head.BaseFeePerGas,
                        BlobGasUsed = head.BlobGasUsed ?? 0,
                        ExcessBlobGas = head.ExcessBlobGas ?? 0,
                        Tx = [.. head.Transactions.Select(t => new TransactionForWeb
                        {
                            Hash = t.Hash,
                            From = t.SenderAddress,
                            To = t.To,
                            TxType = (int)t.Type,
                            MaxPriorityFeePerGas = t.MaxPriorityFeePerGas,
                            MaxFeePerGas = t.MaxFeePerGas,
                            GasPrice = t.GasPrice,
                            GasLimit = t.GasLimit,
                            Nonce = t.Nonce,
                            Value = t.Value,
                            DataLength = t.DataLength,
                            Blobs = t.BlobVersionedHashes?.Length ?? 0,
                            Method = t.DataLength >= 4 ? [.. t.Data.Span[..4]] : []
                        })],
                        Receipts = [.. receipts.Select(r => new ReceiptForWeb
                        {
                            GasUsed = r.GasUsed,
                            EffectiveGasPrice = r.EffectiveGasPrice ?? UInt256.Zero,
                            ContractAddress = r.ContractAddress,
                            Logs = [.. r.Logs.Select(l => new LogEntryForWeb
                            {
                                Address = l.Address,
                                Data = l.Data,
                                Topics = l.Topics
                            })],
                            Status = r.Status,
                            BlobGasPrice = r.BlobGasPrice ?? UInt256.Zero,
                            BlobGasUsed = r.BlobGasUsed ?? 0,
                        })]
                    },
                    Safe = choice.Safe,
                    Finalized = choice.Finalized
                },
                EthereumJsonSerializer.JsonOptions
             )
        );
    }

    private class ForkData
    {
        public BlockForWeb Head { get; set; }
        public long Safe { get; set; }
        public long Finalized { get; set; }
    }

    private class BlockForWeb
    {
        public byte[] ExtraData { get; set; }
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
        public Hash256 Hash { get; set; }
        public Address Beneficiary { get; set; }
        public long Number { get; set; }
        public int Size { get; set; }
        public ulong Timestamp { get; set; }
        public UInt256 BaseFeePerGas { get; set; }
        public ulong BlobGasUsed { get; set; }
        public ulong ExcessBlobGas { get; set; }
        public TransactionForWeb[] Tx { get; set; }
        public ReceiptForWeb[] Receipts { get; set; }
        public ReceiptForWeb[] Withdrawals { get; set; }
    }
    private class ReceiptForWeb
    {
        public long GasUsed { get; set; }
        public UInt256 EffectiveGasPrice { get; set; }
        public Address? ContractAddress { get; set; }
        public LogEntryForWeb[] Logs { get; set; }
        public long? Status { get; set; }
        public UInt256 BlobGasPrice { get; set; }
        public ulong BlobGasUsed { get; set; }
    }
    private class LogEntryForWeb
    {
        public Address Address { get; set; }
        public byte[] Data { get; set; }
        public Hash256[] Topics { get; set; }
    }
    private class TransactionForWeb
    {
        public Hash256 Hash { get; set; }
        public Address From { get; set; }
        public Address? To { get; set; }
        public int TxType { get; set; }
        public UInt256 MaxPriorityFeePerGas { get; set; }
        public UInt256 MaxFeePerGas { get; set; }
        public UInt256 GasPrice { get; set; }
        public long GasLimit { get; set; }
        public UInt256 Nonce { get; set; }
        public UInt256 Value { get; set; }
        public int DataLength { get; set; }
        public int Blobs { get; set; }
        public byte[] Method { get; set; }
    }
    private class WithdrawalForWeb
    {

    }
    private class PeerForWeb
    {
        public AllocationContexts Contexts { get; set; }
        public NodeClientType ClientType { get; set; }
        public int Version { get; set; }
        public long Head { get; set; }
    }

    private DataCompletion _log = new();
    private void OnConsoleLineWritten(object? sender, string logLine)
    {
        // No subscribers, no need to prepare event data
        if (!HaveSubscribers) return;

        DataCompletion log = _log;
        _log = new DataCompletion();

        log.TrySetResult(JsonSerializer.SerializeToUtf8Bytes(new[] { logLine }, JsonSerializerOptions.Web));
    }

    private bool HaveSubscribers => Volatile.Read(ref _subscribers) > 0;
}

internal class SystemStats
{
    public double UserPercent { get; set; }
    public double PrivilegedPercent { get; set; }
    public long Uptime { get; set; }
    public long WorkingSet { get; internal set; }
}
