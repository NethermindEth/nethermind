// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.FlatPriorityRead;

[ConfigCategory(Description = "Serves the flat storage slots of chosen contracts from memory, so their slot reads never reach the flat database.")]
public interface IFlatPriorityReadConfig : IConfig
{
    [ConfigItem(Description = "Enable the flat priority read layer. It only takes effect with flat state.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Comma-separated contract addresses whose storage slots are loaded into memory at startup, at most 255. Startup blocks until they are loaded.", DefaultValue = "null")]
    string? Addresses { get; set; }

    [ConfigItem(Description = "The store holding the slots: Memory (in-process tables), Lmdb or RocksDb (PlainTable over mmap).", DefaultValue = "Memory")]
    FlatPriorityReadBackend Backend { get; set; }

    [ConfigItem(Description = "Directory of the Lmdb and RocksDb stores, wiped at every start. Reads stay in memory only when it is on tmpfs.", DefaultValue = "/dev/shm/nethermind-flat-priority-read")]
    string DbPath { get; set; }

    [ConfigItem(Description = "Lmdb map size in bytes: the most the store can grow to, including pages kept alive for open readers.", DefaultValue = "34359738368")]
    long LmdbMapSize { get; set; }

    [ConfigItem(Description = "The most Lmdb read transactions open at once. Each open flat persistence reader holds one.", DefaultValue = "4096")]
    int LmdbMaxReaders { get; set; }

    [ConfigItem(Description = "RocksDB options appended to the RocksDb store's built-in PlainTable options, for experiments. Must end with ';'.", DefaultValue = "null")]
    string? RocksDbAdditionalOptions { get; set; }
}
