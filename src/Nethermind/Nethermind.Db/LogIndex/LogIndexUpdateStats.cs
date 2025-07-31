// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Db;

public class LogIndexUpdateStats(ILogIndexStorage storage) : IFormattable
{
    private long _blocksAdded;
    private long _txAdded;
    private long _logsAdded;
    private long _topicsAdded;

    public long BlocksAdded => _blocksAdded;
    public long TxAdded => _txAdded;
    public long LogsAdded => _logsAdded;
    public long TopicsAdded => _topicsAdded;

    public long? MaxBlockNumber => storage.GetMaxBlockNumber();
    public long? MinBlockNumber => storage.GetMinBlockNumber();

    public ExecTimeStats SetReceipts { get; } = new();
    public ExecTimeStats Aggregating { get; } = new();
    public ExecTimeStats Processing { get; } = new();

    public ExecTimeStats CallingMerge { get; } = new();
    public ExecTimeStats UpdatingMeta { get; } = new();
    public ExecTimeStats WaitingBatch { get; } = new();
    public ExecTimeStats InMemoryMerging { get; } = new();

    public AverageStats KeysCount { get; } = new();

    public ExecTimeStats QueueingAddressCompression { get; } = new();
    public ExecTimeStats QueueingTopicCompression { get; } = new();

    public PostMergeProcessingStats PostMergeProcessing { get; } = new();
    public CompactingStats Compacting { get; } = new();

    public void Combine(LogIndexUpdateStats other)
    {
        _blocksAdded += other._blocksAdded;
        _txAdded += other._txAdded;
        _logsAdded += other._logsAdded;
        _topicsAdded += other._topicsAdded;

        SetReceipts.Combine(other.SetReceipts);
        Aggregating.Combine(other.Aggregating);
        Processing.Combine(other.Processing);
        UpdatingMeta.Combine(other.UpdatingMeta);
        CallingMerge.Combine(other.CallingMerge);
        WaitingBatch.Combine(other.WaitingBatch);
        InMemoryMerging.Combine(other.InMemoryMerging);
        KeysCount.Combine(other.KeysCount);

        QueueingAddressCompression.Combine(other.QueueingAddressCompression);
        QueueingTopicCompression.Combine(other.QueueingTopicCompression);

        PostMergeProcessing.Combine(other.PostMergeProcessing);
        Compacting.Combine(other.Compacting);
    }

    public void IncrementBlocks() => Interlocked.Increment(ref _blocksAdded);
    public void IncrementTx() => Interlocked.Increment(ref _txAdded);
    public void IncrementLogs() => Interlocked.Increment(ref _logsAdded);
    public void IncrementTopics() => Interlocked.Increment(ref _topicsAdded);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        const string tab = "\t";

        return
            $"""
             {tab}Blocks: {MinBlockNumber:N0} - {MaxBlockNumber:N0} (+{_blocksAdded:N0})

             {tab}Txs: +{_txAdded:N0}
             {tab}Logs: +{_logsAdded:N0}
             {tab}Topics: +{_topicsAdded:N0}

             {tab}Keys per batch: {KeysCount:N0}
             {tab}SetReceipts: {SetReceipts}
             {tab}Aggregating: {Aggregating}
             {tab}Processing: {Processing}

             {tab}Merge call: {CallingMerge}
             {tab}Updating metadata: {UpdatingMeta}
             {tab}Waiting batch: {WaitingBatch}
             {tab}In-memory merging: {InMemoryMerging}

             {tab}Post-merge processing: {PostMergeProcessing.Execution}
             {tab}{tab}DB getting: {PostMergeProcessing.GettingValue}
             {tab}{tab}Compressing: {PostMergeProcessing.CompressingValue}
             {tab}{tab}Putting: {PostMergeProcessing.PuttingValues}
             {tab}{tab}Compressed keys: {PostMergeProcessing.CompressedAddressKeys:N0} address, {PostMergeProcessing.CompressedTopicKeys:N0} topic
             {tab}{tab}In queue: {PostMergeProcessing.QueueLength:N0}

             {tab}Compacting: {Compacting.Total}
             {tab}{tab}Addresses: {Compacting.Addresses}
             {tab}{tab}Topics: {Compacting.Topics}
             """;
    }

    public override string ToString() => ToString(null, null);
}
