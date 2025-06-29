// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public class LogIndexUpdateStats : IFormattable
{
    public long BlocksAdded { get; set; }
    public long TxAdded { get; set; }
    public long LogsAdded { get; set; }
    public long TopicsAdded { get; set; }

    public long MaxBlockNumber { get; set; } = -1;
    public long MinBlockNumber { get; set; } = -1;

    public ExecTimeStats Total { get; } = new();
    public ExecTimeStats BuildingDictionary { get; } = new();
    public ExecTimeStats Processing { get; } = new();

    public ExecTimeStats CallingMerge { get; } = new();
    public ExecTimeStats UpdatingMeta { get; } = new();
    public ExecTimeStats WaitingBatch { get; } = new();
    public ExecTimeStats CompactingDbs { get; } = new();
    public ExecTimeStats FlushingDbs { get; } = new();
    public PostMergeProcessingStats PostMergeProcessing { get; } = new();
    public ExecTimeStats InMemoryMerging { get; } = new();

    public AverageStats KeysCount { get; } = new();

    public ExecTimeStats QueueingAddressCompression { get; } = new();
    public ExecTimeStats QueueingTopicCompression { get; } = new();

    public void Combine(LogIndexUpdateStats other)
    {
        BlocksAdded += other.BlocksAdded;
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;

        Total.Combine(other.Total);
        BuildingDictionary.Combine(other.BuildingDictionary);
        Processing.Combine(other.Processing);
        UpdatingMeta.Combine(other.UpdatingMeta);
        CallingMerge.Combine(other.CallingMerge);
        WaitingBatch.Combine(other.WaitingBatch);
        CompactingDbs.Combine(other.CompactingDbs);
        FlushingDbs.Combine(other.FlushingDbs);
        PostMergeProcessing.Combine(other.PostMergeProcessing);
        InMemoryMerging.Combine(other.InMemoryMerging);
        KeysCount.Combine(other.KeysCount);
        MaxBlockNumber = Math.Max(MaxBlockNumber, other.MaxBlockNumber);
        MinBlockNumber = Math.Min(MinBlockNumber, other.MinBlockNumber);

        QueueingAddressCompression.Combine(other.QueueingAddressCompression);
        QueueingTopicCompression.Combine(other.QueueingTopicCompression);
    }

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        const string tab = "\t";

        return
            $"""
             {tab}Blocks: {MinBlockNumber:N0} - {MaxBlockNumber:N0}

             {tab}Txs: +{TxAdded:N0}
             {tab}Logs: +{LogsAdded:N0}
             {tab}Topics: +{TopicsAdded:N0}

             {tab}Keys per batch: {KeysCount:N0}
             {tab}Total: {Total}
             {tab}Building dictionary: {BuildingDictionary}
             {tab}Processing: {Processing}

             {tab}Merge call: {CallingMerge}
             {tab}Updating metadata: {UpdatingMeta}
             {tab}Waiting batch: {WaitingBatch}
             {tab}In-memory merging: {InMemoryMerging}

             {tab}Flushing DBs: {FlushingDbs}
             {tab}Compacting DBs: {CompactingDbs}
             {tab}Post-merge processing: {PostMergeProcessing.Execution}
             {tab}{tab}DB getting: {PostMergeProcessing.GettingValue}
             {tab}{tab}Compressing: {PostMergeProcessing.CompressingValue}
             {tab}{tab}Putting: {PostMergeProcessing.PuttingValues}
             {tab}{tab}Compressed keys: {PostMergeProcessing.CompressedAddressKeys:N0} address, {PostMergeProcessing.CompressedTopicKeys:N0} topic
             {tab}{tab}In queue: {PostMergeProcessing.QueueLength:N0}
             """;
    }

    public override string ToString() => ToString(null, null);
}
