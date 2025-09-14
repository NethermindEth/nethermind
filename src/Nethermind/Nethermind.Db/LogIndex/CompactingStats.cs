// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public class CompactingStats
{
    public ExecTimeStats Total { get; set; } = new();

    public void Combine(CompactingStats other)
    {
        Total.Combine(other.Total);
    }
}
