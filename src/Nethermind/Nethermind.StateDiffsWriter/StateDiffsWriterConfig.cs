// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateDiffsWriter;

public class StateDiffsWriterConfig : IStateDiffsWriterConfig
{
    public bool Enabled { get; set; }
    public long KeepLastNBlocks { get; set; } = 1_000_000;
    public int PruneIntervalSeconds { get; set; } = 600;
}
