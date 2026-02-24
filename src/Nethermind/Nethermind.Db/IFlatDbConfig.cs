// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface IFlatDbConfig : IConfig
{
    [ConfigItem(Description = "Block cache size budget", DefaultValue = "1073741824")]
    long BlockCacheSizeBudget { get; set; }

    [ConfigItem(Description = "Compact size", DefaultValue = "32")]
    int CompactSize { get; set; }

    [ConfigItem(Description = "Enabled", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Enable recording of preimages (address/slot hash to original bytes)", DefaultValue = "false")]
    bool EnablePreimageRecording { get; set; }

    [ConfigItem(Description = "Import from pruning trie state db", DefaultValue = "false")]
    bool ImportFromPruningTrieState { get; set; }

    [ConfigItem(Description = "Inline compaction", DefaultValue = "false")]
    bool InlineCompaction { get; set; }

    [ConfigItem(Description = "Flat db layout", DefaultValue = "Flat")]
    FlatLayout Layout { get; set; }

    [ConfigItem(Description = "Max in flight compact job", DefaultValue = "32")]
    int MaxInFlightCompactJob { get; set; }

    [ConfigItem(Description = "Max in-memory reorg depth before converting to persisted snapshots", DefaultValue = "256")]
    int MaxInMemoryReorgDepth { get; set; }

    [ConfigItem(Description = "Minimum compact size (power of 2, floor for hierarchical compaction)", DefaultValue = "4")]
    int MinCompactSize { get; set; }

    [ConfigItem(Description = "Minimum reorg depth", DefaultValue = "128")]
    int MinReorgDepth { get; set; }

    [ConfigItem(Description = "Trie cache memory target", DefaultValue = "536870912")]
    long TrieCacheMemoryBudget { get; set; }

    [ConfigItem(Description = "Trie warmer worker count (-1 for processor count - 1, 0 to disable)", DefaultValue = "-1")]
    int TrieWarmerWorkerCount { get; set; }

    [ConfigItem(Description = "Verify with trie", DefaultValue = "false")]
    bool VerifyWithTrie { get; set; }

    [ConfigItem(Description = "Enable long finality support with persisted snapshots", DefaultValue = "false")]
    bool EnableLongFinality { get; set; }

    [ConfigItem(Description = "Total max reorg depth (in-memory + persisted). When exceeded, force-persist oldest HSST snapshot to RocksDB.", DefaultValue = "90000")]
    int LongFinalityReorgDepth { get; set; }

    [ConfigItem(Description = "Path for persisted snapshot arena files (relative to data dir)", DefaultValue = "snapshots")]
    string PersistedSnapshotPath { get; set; }

    [ConfigItem(Description = "Max arena file size in bytes", DefaultValue = "4294967296")]
    long ArenaFileSizeBytes { get; set; }

    [ConfigItem(Description = "Max persisted snapshot compaction size (hierarchical compaction ceiling for persisted layer)", DefaultValue = "1024")]
    int PersistedSnapshotMaxCompactSize { get; set; }
}
