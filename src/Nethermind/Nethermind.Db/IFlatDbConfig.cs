// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface IFlatDbConfig: IConfig
{
    [ConfigItem(Description = "Enabled", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Import from pruning trie state db", DefaultValue = "false")]
    bool ImportFromPruningTrieState { get; set; }

    [ConfigItem(Description = "Pruning boundary", DefaultValue = "256")]
    int PruningBoundary { get; set; }

    [ConfigItem(Description = "Compact size", DefaultValue = "16")]
    int CompactSize { get; set; }

    [ConfigItem(Description = "Compact interval", DefaultValue = "4")]
    int MidCompactSize { get; set; }

    [ConfigItem(Description = "Max in flight compact job", DefaultValue = "32")]
    int MaxInFlightCompactJob { get; set; }

    [ConfigItem(Description = "Read with try", DefaultValue = "false")]
    bool ReadWithTrie { get; set; }

    [ConfigItem(Description = "Verify with trie", DefaultValue = "false")]
    bool VerifyWithTrie { get; set; }

    [ConfigItem(Description = "Inline compaction", DefaultValue = "false")]
    bool InlineCompaction { get; set; }

    [ConfigItem(Description = "Trie cache memory target", DefaultValue = "false")]
    long TrieCacheMemoryTarget { get; set; }

    [ConfigItem(Description = "Use preimage", DefaultValue = "false")]
    FlatLayout Layout { get; set; }

    [ConfigItem(Description = "Block cache size budget", DefaultValue = "1000000000")]
    long BlockCacheSizeBudget { get; set; }

    [ConfigItem(Description = "Import to flat on state sync finished", DefaultValue = "false")]
    bool ImportOnStateSyncFinished { get; set; }

    [ConfigItem(Description = "Generate preimage", DefaultValue = "false")]
    bool GeneratePreimage { get; set; }

    [ConfigItem(Description = "Max pruning boundary", DefaultValue = "false")]
    int MaxPruningBoundary { get; set; }

    [ConfigItem(Description = "Use flat bloom", DefaultValue = "false")]
    bool EnableFlatBloom { get; set; }

    [ConfigItem(Description = "Warmup key by key", DefaultValue = "false")]
    bool WarmUpPersistence { get; set; }

    [ConfigItem(Description = "Trie warmer worker count (-1 for processor count - 1, 0 to disable)", DefaultValue = "-1")]
    int TrieWarmerWorkerCount { get; set; }
}
