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

    public void Add(SetReceiptsStats other)
    {
        TxAdded += other.TxAdded;
        LogsAdded += other.LogsAdded;
        TopicsAdded += other.TopicsAdded;

        SeekForPrevHit.Add(other.SeekForPrevHit);
        SeekForPrevMiss.Add(other.SeekForPrevMiss);
    }
}
