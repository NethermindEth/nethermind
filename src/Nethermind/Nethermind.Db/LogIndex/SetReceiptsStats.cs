// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public class SetReceiptsStats
{
    public long BlocksAdded { get; set; }
    public long TxAdded { get; set; }
    public long LogsAdded { get; set; }
    public long TopicsAdded { get; set; }

    public ExecTimeStats SeekForPrevHit { get; set; } = new();
    public ExecTimeStats SeekForPrevMiss { get; set; } = new();
    public ExecTimeStats BuildingDictionary { get; set; } = new();
    public ExecTimeStats WaitingForFinalization { get; set; } = new();
    public ExecTimeStats FlushingDbs { get; set; } = new();
    public AverageStats KeysCount { get; set; } = new();
    public AverageStats BytesWritten { get; set; } = new();

    public void Combine(SetReceiptsStats other)
    {
        BlocksAdded += other.BlocksAdded;
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;

        SeekForPrevHit.Combine(other.SeekForPrevHit);
        SeekForPrevMiss.Combine(other.SeekForPrevMiss);
        BuildingDictionary.Combine(other.BuildingDictionary);
        WaitingForFinalization.Combine(other.WaitingForFinalization);
        FlushingDbs.Combine(other.FlushingDbs);
        KeysCount.Combine(other.KeysCount);
        BytesWritten.Combine(other.BytesWritten);
    }
}
