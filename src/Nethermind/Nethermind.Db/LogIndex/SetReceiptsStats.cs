// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public class SetReceiptsStats
{
    public long BlocksAdded { get; set; }
    public long TxAdded { get; set; }
    public long LogsAdded { get; set; }
    public long TopicsAdded { get; set; }

    public ExecTimeStats SeekForPrevHit { get; } = new();
    public ExecTimeStats SeekForPrevMiss { get; } = new();
    public ExecTimeStats BuildingDictionary { get; } = new();
    public ExecTimeStats ProcessingData { get; } = new();
    public ExecTimeStats WaitingForFinalization { get; } = new();
    public ExecTimeStats FlushingDbs { get; } = new();
    public AverageStats KeysCount { get; } = new();
    public AverageStats BytesWritten { get; } = new();
    public long NewDbIndexes;
    public long NewTempIndexes;
    public long NewTempFromDbIndexes;

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
        WaitingForFinalization.Combine(other.WaitingForFinalization);
        FlushingDbs.Combine(other.FlushingDbs);
        KeysCount.Combine(other.KeysCount);
        BytesWritten.Combine(other.BytesWritten);
        NewDbIndexes += other.NewDbIndexes;
        NewTempIndexes += other.NewTempIndexes;
        NewTempFromDbIndexes += other.NewTempFromDbIndexes;
    }
}
