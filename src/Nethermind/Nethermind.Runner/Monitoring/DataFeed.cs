// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Runner.Monitoring.TransactionPool;
using Nethermind.TxPool;

namespace Nethermind.Runner.Monitoring;

public class DataFeed
{
    public static long StartTime { get; set; }

    private readonly ITxPool _txPool;
    private readonly IBlockTree _blockTree;

    public DataFeed(INethermindApi api)
    {
        _txPool = api.TxPool;
        _blockTree = api.BlockTree;

        ArgumentNullException.ThrowIfNull(_txPool);
        ArgumentNullException.ThrowIfNull(_blockTree);

        api.BlockchainProcessor.NewProcessingStatistics += OnNewProcessingStatistics;
        _blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
        ConsoleHelpers.LineWritten += OnConsoleLineWritten;
        _ = StartTxFlowRefresh();
        _ = SystemStatsRefresh();
    }

    public async Task ProcessingFeed(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";

        await ctx.Response.WriteAsync($"event: nodeData\n", cancellationToken: ct);
        await ctx.Response.WriteAsync($"data: ", cancellationToken: ct);
        await ctx.Response.Body.WriteAsync(GetNodeData(), cancellationToken: ct);
        await ctx.Response.WriteAsync($"\n\n", cancellationToken: ct);

        await ctx.Response.WriteAsync($"event: txNodes\n", cancellationToken: ct);
        await ctx.Response.WriteAsync($"data: ", cancellationToken: ct);
        await ctx.Response.Body.WriteAsync(TxPoolFlow.NodeJson, cancellationToken: ct);
        await ctx.Response.WriteAsync($"\n\n", cancellationToken: ct);

        await ctx.Response.WriteAsync($"event: log\n", cancellationToken: ct);
        await ctx.Response.WriteAsync($"data: ", cancellationToken: ct);
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ConsoleHelpers.GetRecentMessages(), JsonSerializerOptions.Web), cancellationToken: ct);
        await ctx.Response.WriteAsync($"\n\n", cancellationToken: ct);

        var channel = Channel.CreateUnbounded<ChannelEntry>();

        InitializeChannelSubscriptions(channel, ct);

        await foreach (ChannelEntry entry in channel.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"event: {entry.Type}\n", cancellationToken: ct);
            await ctx.Response.WriteAsync($"data: ", cancellationToken: ct);
            await ctx.Response.Body.WriteAsync(entry.Data, cancellationToken: ct);
            await ctx.Response.WriteAsync($"\n\n", cancellationToken: ct);

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
        system
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
                    TxPool.Metrics.TransactionsSourcedPrivateOrderflow,
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

    TaskCompletionSource<byte[]> _forkChoice = new();
    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoice choice)
    {
        TaskCompletionSource<byte[]> forkChoice = _forkChoice;
        _forkChoice = new TaskCompletionSource<byte[]>();

        forkChoice.TrySetResult(JsonSerializer.SerializeToUtf8Bytes(choice, JsonSerializerOptions.Web));
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
