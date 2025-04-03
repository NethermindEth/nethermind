// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public class SetReceiptsStats
{
    public long BlocksAdded { get; set; }
    public long TxAdded { get; set; }
    public long LogsAdded { get; set; }
    public long TopicsAdded { get; set; }
    public long LastBlockNumber { get; set; } = -1;

    public ExecTimeStats BuildingDictionary { get; } = new();
    public ExecTimeStats Processing { get; } = new();

    public long CompressedAddressKeys { get; set; }
    public long CompressedTopicKeys { get; set; }
    public ExecTimeStats CallingMerge { get; } = new();
    public ExecTimeStats CompactingDbs { get; } = new();
    public ExecTimeStats FlushingDbs { get; } = new();
    public ExecTimeStats PostMergeProcessing { get; } = new();

    public AverageStats KeysCount { get; } = new();
    public ExecTimeStats WaitingBatch { get; } = new();

    public ExecTimeStats QueueingAddressCompression { get; } = new();
    public ExecTimeStats QueueingTopicCompression { get; } = new();

    public void Combine(SetReceiptsStats other)
    {
        BlocksAdded += other.BlocksAdded;
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;
        CompressedAddressKeys += other.CompressedAddressKeys;
        CompressedTopicKeys += other.CompressedTopicKeys;

        BuildingDictionary.Combine(other.BuildingDictionary);
        Processing.Combine(other.Processing);
        CallingMerge.Combine(other.CallingMerge);
        CompactingDbs.Combine(other.CompactingDbs);
        FlushingDbs.Combine(other.FlushingDbs);
        PostMergeProcessing.Combine(other.PostMergeProcessing);
        KeysCount.Combine(other.KeysCount);
        LastBlockNumber = Math.Max(LastBlockNumber, other.LastBlockNumber);
        WaitingBatch.Combine(other.WaitingBatch);

        QueueingAddressCompression.Combine(other.QueueingAddressCompression);
        QueueingTopicCompression.Combine(other.QueueingTopicCompression);
    }
}
