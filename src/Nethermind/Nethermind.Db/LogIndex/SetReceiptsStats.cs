// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public class SetReceiptsStats
{
    public long BlocksAdded { get; set; }
    public long TxAdded { get; set; }
    public long LogsAdded { get; set; }
    public long TopicsAdded { get; set; }
    public long KeysCount { get; set; }

    public ExecTimeStats SeekForPrevHit { get; set; } = new();
    public ExecTimeStats SeekForPrevMiss { get; set; } = new();
    public ExecTimeStats BuildingDictionary { get; set; } = new();
    public ExecTimeStats WaitingForFinalization { get; set; } = new();
    public AverageStats BytesWritten { get; set; } = new();

    public void Combine(SetReceiptsStats other)
    {
        BlocksAdded += other.BlocksAdded;
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;
        KeysCount += other.KeysCount; // very-very rough estimation

        SeekForPrevHit.Combine(other.SeekForPrevHit);
        SeekForPrevMiss.Combine(other.SeekForPrevMiss);
        BuildingDictionary.Combine(other.BuildingDictionary);
        WaitingForFinalization.Combine(other.WaitingForFinalization);
        BytesWritten.Combine(other.BytesWritten);
    }
}
