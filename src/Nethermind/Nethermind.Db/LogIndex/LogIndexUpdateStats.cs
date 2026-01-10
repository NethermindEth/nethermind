// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Db.LogIndex;

/// <summary>
/// Log index building statistics across some time range.
/// </summary>
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

    public long? MaxBlockNumber => storage.MaxBlockNumber;
    public long? MinBlockNumber => storage.MinBlockNumber;

    public ExecTimeStats Adding { get; } = new();
    public ExecTimeStats Aggregating { get; } = new();
    public ExecTimeStats Merging { get; } = new();

    public ExecTimeStats DBMerging { get; } = new();
    public ExecTimeStats UpdatingMeta { get; } = new();
    public ExecTimeStats CommitingBatch { get; } = new();
    public ExecTimeStats BackgroundMerging { get; } = new();

    public AverageStats KeysCount { get; } = new();

    public ExecTimeStats QueueingAddressCompression { get; } = new();
    public ExecTimeStats QueueingTopicCompression { get; } = new();

    public PostMergeProcessingStats Compressing { get; } = new();
    public CompactingStats Compacting { get; } = new();

    public ExecTimeStats LoadingReceipts { get; } = new();

    public void Combine(LogIndexUpdateStats other)
    {
        _blocksAdded += other._blocksAdded;
        _txAdded += other._txAdded;
        _logsAdded += other._logsAdded;
        _topicsAdded += other._topicsAdded;

        Adding.Combine(other.Adding);
        Aggregating.Combine(other.Aggregating);
        Merging.Combine(other.Merging);
        UpdatingMeta.Combine(other.UpdatingMeta);
        DBMerging.Combine(other.DBMerging);
        CommitingBatch.Combine(other.CommitingBatch);
        BackgroundMerging.Combine(other.BackgroundMerging);
        KeysCount.Combine(other.KeysCount);

        QueueingAddressCompression.Combine(other.QueueingAddressCompression);
        QueueingTopicCompression.Combine(other.QueueingTopicCompression);

        Compressing.Combine(other.Compressing);
        Compacting.Combine(other.Compacting);

        LoadingReceipts.Combine(other.LoadingReceipts);
    }

    public void IncrementBlocks() => Interlocked.Increment(ref _blocksAdded);
    public void IncrementTx(int count = 1) => Interlocked.Add(ref _txAdded, count);
    public void IncrementLogs() => Interlocked.Increment(ref _logsAdded);
    public void IncrementTopics() => Interlocked.Increment(ref _topicsAdded);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        const string tab = "\t";

        return !string.Equals(format, "D", StringComparison.OrdinalIgnoreCase)
            ? $"{MinBlockNumber:N0} - {MaxBlockNumber:N0} (blocks: +{BlocksAdded:N0} | txs: +{TxAdded:N0} | logs: +{LogsAdded:N0} | topics: +{TopicsAdded:N0})"
            : $"""

               {tab}Blocks: {MinBlockNumber:N0} - {MaxBlockNumber:N0} (+{_blocksAdded:N0})

               {tab}Txs: +{TxAdded:N0}
               {tab}Logs: +{LogsAdded:N0}
               {tab}Topics: +{TopicsAdded:N0}

               {tab}Keys per batch: {KeysCount:N0}

               {tab}Loading receipts: {LoadingReceipts}
               {tab}Aggregating: {Aggregating}

               {tab}Adding receipts: {Adding}
               {tab}{tab}Merging: {Merging} (DB: {DBMerging})
               {tab}{tab}Updating metadata: {UpdatingMeta}
               {tab}{tab}Commiting batch: {CommitingBatch}

               {tab}Background merging: {BackgroundMerging}

               {tab}Post-merge compression: {Compressing.Total}
               {tab}{tab}DB reading: {Compressing.DBReading}
               {tab}{tab}Compressing: {Compressing.CompressingValue}
               {tab}{tab}DB saving: {Compressing.DBSaving}
               {tab}{tab}Keys compressed: {Compressing.CompressedAddressKeys:N0} address, {Compressing.CompressedTopicKeys:N0} topic
               {tab}{tab}Keys in queue: {Compressing.QueueLength:N0}

               {tab}Compacting: {Compacting.Total}
               """;
    }

    public override string ToString() => ToString(null, null);
}
