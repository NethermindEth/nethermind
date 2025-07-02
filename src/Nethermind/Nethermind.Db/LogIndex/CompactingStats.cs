// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public class CompactingStats
{
    public ExecTimeStats Total { get; set; } = new();
    public ExecTimeStats Addresses { get; set; } = new();
    public ExecTimeStats Topics { get; set; } = new();

    public void Combine(CompactingStats other)
    {
        Addresses.Combine(other.Addresses);
        Topics.Combine(other.Topics);
        Total.Combine(other.Total);
    }
}
