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
    public long NewDBKeys;

    public ExecTimeStats SeekForPrevHit { get; } = new();
    public ExecTimeStats SeekForPrevMiss { get; } = new();
    public ExecTimeStats BuildingDictionary { get; } = new();
    public ExecTimeStats ProcessingData { get; } = new();

    public long AddressMerges { get; set; }
    public long TopicMerges { get; set; }
    public ExecTimeStats CallingMerge { get; } = new();
    public ExecTimeStats CompactingDbs { get; } = new();
    public ExecTimeStats CreatingPostMergeKeys { get; } = new();

    public AverageStats KeysCount { get; } = new();

    public void Combine(SetReceiptsStats other)
    {
        BlocksAdded += other.BlocksAdded;
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;
        AddressMerges += other.AddressMerges;
        TopicMerges += other.TopicMerges;
        NewDBKeys += other.NewDBKeys;

        SeekForPrevHit.Combine(other.SeekForPrevHit);
        SeekForPrevMiss.Combine(other.SeekForPrevMiss);
        BuildingDictionary.Combine(other.BuildingDictionary);
        ProcessingData.Combine(other.ProcessingData);
        CallingMerge.Combine(other.CallingMerge);
        CompactingDbs.Combine(other.CompactingDbs);
        CreatingPostMergeKeys.Combine(other.CreatingPostMergeKeys);
        KeysCount.Combine(other.KeysCount);
        LastBlockNumber = Math.Max(LastBlockNumber, other.LastBlockNumber);
    }
}
