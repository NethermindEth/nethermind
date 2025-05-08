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

namespace Nethermind.Runner.Monitoring;

public class DataFeed
{
    public static long StartTime { get; set; }

    private readonly ITxPool _txPool;
    private readonly ISpecProvider _specProvider;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IBlockTree _blockTree;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly BlockDecoder _blockDecoder = new();
    private readonly ILogger _logger;

    public DataFeed(
        ITxPool txPool,
        ISpecProvider specProvider,
        IReceiptFinder receiptFinder,
        IBlockTree blockTree,
        ISyncPeerPool syncPeerPool,
        IMainProcessingContext mainProcessingContext,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(txPool);
        ArgumentNullException.ThrowIfNull(syncPeerPool);
        ArgumentNullException.ThrowIfNull(receiptFinder);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(syncPeerPool);
        ArgumentNullException.ThrowIfNull(mainProcessingContext?.BlockchainProcessor);

        _txPool = txPool;
        _specProvider = specProvider;
        _receiptFinder = receiptFinder;
        _blockTree = blockTree;
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
    }

    private async Task ProcessingFeeds(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await ctx.Response.WriteAsync("event: nodeData\ndata: ", cancellationToken: ct);
        await ctx.Response.Body.WriteAsync(GetNodeData(), cancellationToken: ct);
        await ctx.Response.WriteAsync("\n\n", cancellationToken: ct);

        await ctx.Response.WriteAsync("event: txNodes\ndata: ", cancellationToken: ct);
        await ctx.Response.Body.WriteAsync(TxPoolFlow.NodeJson, cancellationToken: ct);
        await ctx.Response.WriteAsync("\n\n", cancellationToken: ct);

        await ctx.Response.WriteAsync("event: log\ndata: ", cancellationToken: ct);
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ConsoleHelpers.GetRecentMessages(), JsonSerializerOptions.Web), cancellationToken: ct);
        await ctx.Response.WriteAsync("\n\n", cancellationToken: ct);

        var channel = Channel.CreateUnbounded<ChannelEntry>();

        InitializeChannelSubscriptions(channel, ct);

        await foreach (ChannelEntry entry in channel.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"event: {entry.Type}\ndata: ", cancellationToken: ct);
            await ctx.Response.Body.WriteAsync(entry.Data, cancellationToken: ct);
            await ctx.Response.WriteAsync("\n\n", cancellationToken: ct);

            if (channel.Reader.Count == 0)
            {
                await ctx.Response.Body.FlushAsync(cancellationToken: ct);
            }
        }
    }

    enum EntryType
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

    class ChannelEntry
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

    private byte[] GetNodeData()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new NethermindNodeData(uptime: Environment.TickCount64 - DataFeed.StartTime),
            JsonSerializerOptions.Web);
    }

    TaskCompletionSource<byte[]> _txFlow = new();
    private async Task StartTxFlowRefresh()
    {
        while (true)
        {
            byte[] data = await GetTxFlowTask(delayMs: 1000);

            var txFlow = _txFlow;
            _txFlow = new TaskCompletionSource<byte[]>();
            txFlow.TrySetResult(data);
        }
    }

    Environment.ProcessCpuUsage _lastCpuUsage;
    long _lastTimeStamp;
    TaskCompletionSource<byte[]> _systemStats = new();
    private async Task SystemStatsRefresh()
    {
        _lastCpuUsage = Environment.CpuUsage;
        _lastTimeStamp = Stopwatch.GetTimestamp();
        while (true)
        {
            var data = await GetStatsTask(delayMs: 1000);
            var systemStats = _systemStats;
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

        var stats = new SystemStats
        {
            UserPercent = ((cpuUsage.UserTime - _lastCpuUsage.UserTime).TotalMicroseconds / elapsed.TotalMicroseconds) / Environment.ProcessorCount,
            PrivilegedPercent = ((cpuUsage.PrivilegedTime - _lastCpuUsage.PrivilegedTime).TotalMicroseconds / elapsed.TotalMicroseconds) / Environment.ProcessorCount,
            WorkingSet = Environment.WorkingSet,
            Uptime = Environment.TickCount64 - DataFeed.StartTime
        };
        _lastTimeStamp = timeStamp;
        _lastCpuUsage = cpuUsage;

        return JsonSerializer.SerializeToUtf8Bytes(stats, JsonSerializerOptions.Web);
    }

    TaskCompletionSource<byte[]> _peers = new();
    private async Task StartPeersRefresh()
    {
        _lastCpuUsage = Environment.CpuUsage;
        _lastTimeStamp = Stopwatch.GetTimestamp();
        while (true)
        {
            var data = await GetPeersTask(delayMs: 1000);
            var peers = _peers;
            _peers = new();
            peers.TrySetResult(data);
        }
    }

    private async Task<byte[]> GetPeersTask(int delayMs)
    {
        await Task.Delay(delayMs);

        var allPeers = _syncPeerPool.AllPeers;
        List<PeerForWeb> peers = [];
        foreach (PeerInfo peer in _syncPeerPool.AllPeers)
        {
            peers.Add(new PeerForWeb
            {
                Contexts = peer.AllocatedContexts,
                ClientType = peer.PeerClientType,
                Version = peer.SyncPeer.ProtocolVersion,
                Head = peer.HeadNumber
            });
        }

        return JsonSerializer.SerializeToUtf8Bytes(peers, JsonSerializerOptions.Web);
    }

    private async Task<byte[]> GetTxFlowTask(int delayMs)
    {
        await Task.Delay(delayMs);
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

    TaskCompletionSource<byte[]> _processing = new();
    private void OnNewProcessingStatistics(object? sender, BlockStatistics stats)
    {
        TaskCompletionSource<byte[]> processing = _processing;
        _processing = new TaskCompletionSource<byte[]>();

        processing.TrySetResult(JsonSerializer.SerializeToUtf8Bytes(stats, JsonSerializerOptions.Web));
    }

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoice choice)
    {
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

    TaskCompletionSource<byte[]> _forkChoice = new();
    private void OnForkChoiceUpdated(IBlockTree.ForkChoice choice)
    {
        TaskCompletionSource<byte[]> forkChoice = _forkChoice;
        _forkChoice = new TaskCompletionSource<byte[]>();

        var head = choice.Head;
        Transaction[] txs = choice.Head.Transactions;
        IReleaseSpec spec = _specProvider.GetSpec(choice.Head.Header);
        var receipts = _receiptFinder.Get(choice.Head).Select((r, i) => new ReceiptForRpc(txs[i].Hash, r, txs[i].GetGasInfo(spec, choice.Head.Header))).ToArray();
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
                        Tx = head.Transactions.Select(t => new TransactionForWeb
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
                            Method = t.Data.HasValue && t.DataLength >= 4 ? t.Data.Value.Span[..4].ToArray() : []
                        }).ToArray(),
                        Receipts = receipts.Select(r => new ReceiptForWeb
                        {
                            GasUsed = r.GasUsed,
                            EffectiveGasPrice = r.EffectiveGasPrice ?? UInt256.Zero,
                            ContractAddress = r.ContractAddress,
                            Logs = r.Logs.Select(l => new LogEntryForWeb
                            {
                                Address = l.Address,
                                Data = l.Data,
                                Topics = l.Topics
                            }).ToArray(),
                            Status = r.Status,
                            BlobGasPrice = r.BlobGasPrice ?? UInt256.Zero,
                            BlobGasUsed = r.BlobGasUsed ?? 0,
                        }).ToArray()
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
        public long Status { get; set; }
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

    TaskCompletionSource<byte[]> _log = new();
    private void OnConsoleLineWritten(object? sender, string logLine)
    {
        TaskCompletionSource<byte[]> log = _log;
        _log = new TaskCompletionSource<byte[]>();

        log.TrySetResult(JsonSerializer.SerializeToUtf8Bytes(new[] { logLine }, JsonSerializerOptions.Web));
    }
}

internal class SystemStats
{
    public double UserPercent { get; set; }
    public double PrivilegedPercent { get; set; }
    public long Uptime { get; set; }
    public long WorkingSet { get; internal set; }
}
