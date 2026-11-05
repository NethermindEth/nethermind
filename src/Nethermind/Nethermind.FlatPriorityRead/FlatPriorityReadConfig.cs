// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.FlatPriorityRead;

public class FlatPriorityReadConfig : IFlatPriorityReadConfig
{
    public bool Enabled { get; set; }
    public Dictionary<string, string>? Addresses { get; set; }
    public FlatPriorityReadBackend Backend { get; set; } = FlatPriorityReadBackend.Memory;
    public bool CacheTrie { get; set; }
    public string DbPath { get; set; } = "/dev/shm/nethermind-flat-priority-read";
    public long LmdbMapSize { get; set; } = 34359738368;
    public int LmdbMaxReaders { get; set; } = 4096;
    public string? RocksDbAdditionalOptions { get; set; }
}
