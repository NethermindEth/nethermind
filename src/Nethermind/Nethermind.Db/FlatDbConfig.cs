// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Extensions;

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
    int CompactInterval { get; set; }

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

    [ConfigItem(Description = "Disable trie warmer", DefaultValue = "false")]
    bool DisableTrieWarmer { get; set; }
}

public class FlatDbConfig: IFlatDbConfig
{
    public bool Enabled { get; set; } = false;
    public bool ImportFromPruningTrieState { get; set; } = false;
    public int PruningBoundary { get; set; } = 256;
    public int CompactSize { get; set; } = 16;
    public int CompactInterval { get; set; } = 4;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public bool ReadWithTrie { get; set; } = false;
    public bool VerifyWithTrie { get; set; } = false;
    public bool InlineCompaction { get; set; } = false;

    // 1 GB is enough for 19% dirty load. Without it, then the diff layers on its own have around 35% dirty load.
    public long TrieCacheMemoryTarget { get; set; } = 1.GiB();
    public bool DisableTrieWarmer { get; set; } = false;
}
