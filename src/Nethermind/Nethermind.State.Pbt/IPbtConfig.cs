// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.State.Pbt;

public interface IPbtConfig : IConfig
{
    [ConfigItem(Description = "Whether to use the experimental EIP-8297 partitioned binary tree state backend. The state root will not match networks using the hexary Patricia trie.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The number of in-memory snapshots (one per block) merged into a single snapshot by compaction, and the persist batch granularity, in blocks.", DefaultValue = "32")]
    int CompactSize { get; set; }

    [ConfigItem(Description = "Shifts the compaction and persistence boundaries so nodes do not all compact on the same blocks. Negative generates one per node on first run and stores it; any other value is used as-is, in blocks.", DefaultValue = "-1")]
    long CompactionOffset { get; set; }

    [ConfigItem(Description = "The minimum depth, in blocks, that a state must be below the head before it may be persisted to disk.", DefaultValue = "128")]
    int MinReorgDepth { get; set; }

    [ConfigItem(Description = "The depth, in blocks, past which states are force-persisted even without finality, bounding memory use, in blocks.", DefaultValue = "256")]
    int MaxReorgDepth { get; set; }

    [ConfigItem(Description = "Rebuild the PBT state from an existing preimage-flat state database, then exit. Requires a fully synced FlatLayout.PreimageFlat 'flat' database (and the 'code' database) in the data directory.", DefaultValue = "false")]
    bool ImportFromPreimageFlat { get; set; }

    [ConfigItem(Description = "Whether to store only the even levels of each 4-level trie node tile, folding the odd levels' hashes on demand. Reduces the size of the trie node column at a small rebuild cost. The state root is identical either way, and both layouts remain readable regardless of this setting.", DefaultValue = "true")]
    bool InterleaveTrieNodeLevels { get; set; }
}
