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

    public ExecTimeStats SeekForPrevHit { get; } = new();
    public ExecTimeStats SeekForPrevMiss { get; } = new();
    public ExecTimeStats BuildingDictionary { get; } = new();
    public ExecTimeStats ProcessingData { get; } = new();
    public ExecTimeStats WaitingPage { get; } = new();
    public ExecTimeStats StoringIndex { get; } = new();
    public ExecTimeStats WritingTemp { get; } = new();
    public ExecTimeStats WaitingForFinalization { get; } = new();
    public ExecTimeStats FlushingDbs { get; } = new();
    public ExecTimeStats FlushingTemp { get; } = new();
    public AverageStats KeysCount { get; } = new();
    public AverageStats BytesWritten { get; } = new();
    public long NewTempIndexes;
    public long NewFinalIndexes;

    public void Combine(SetReceiptsStats other)
    {
        BlocksAdded += other.BlocksAdded;
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;

        SeekForPrevHit.Combine(other.SeekForPrevHit);
        SeekForPrevMiss.Combine(other.SeekForPrevMiss);
        BuildingDictionary.Combine(other.BuildingDictionary);
        ProcessingData.Combine(other.ProcessingData);
        WaitingPage.Combine(other.WaitingPage);
        StoringIndex.Combine(other.StoringIndex);
        WritingTemp.Combine(other.WritingTemp);
        WaitingForFinalization.Combine(other.WaitingForFinalization);
        FlushingDbs.Combine(other.FlushingDbs);
        FlushingTemp.Combine(other.FlushingTemp);
        KeysCount.Combine(other.KeysCount);
        BytesWritten.Combine(other.BytesWritten);
        NewTempIndexes += other.NewTempIndexes;
        NewFinalIndexes += other.NewFinalIndexes;
        LastBlockNumber = Math.Max(LastBlockNumber, other.LastBlockNumber);
    }
}
