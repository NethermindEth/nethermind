// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Pbt;

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

    [ConfigItem(Description = "Number of parallel workers copying the source into the PBT flat columns during the preimage-flat import, each claiming account key ranges in turn. 0 uses the processor count. Only parallelizes that copy; the leaf derivation and the tree fold stay single-threaded.", DefaultValue = "0")]
    int ImportStorageReadConcurrency { get; set; }

    [ConfigItem(Description = "Number of tree leaves buffered per window during the preimage-flat import before it is folded into the tree and committed. 0 uses the built-in default (2000000). Larger windows fold in fewer passes at the cost of memory.", DefaultValue = "0")]
    int ImportWindowSize { get; set; }

    [ConfigItem(Description = "Report what the persisted PBT database holds - the trie's shape by depth, and how many nodes the interleaved encoding, the node chains and the leaf blobs each leave unstored - then exit.", DefaultValue = "false")]
    bool ScanTree { get; set; }

    [ConfigItem(Description = "Number of parallel workers sweeping each column during the tree scan, each claiming key ranges in turn. 0 uses the processor count. The columns are still scanned one after another.", DefaultValue = "0")]
    int ScanTreeConcurrency { get; set; }

    [ConfigItem(Description = "Which levels of each 4-level trie node tile and of each stem's 256-leaf subtree store a node, the rest being folded on demand: `EveryLevel` stores all of them, `Interleaved` only the even ones, `BoundaryOnly` none at all - just the tile's boundary entries and the stem's leaves - and `Every4Depth` keeps the boundary-only tile but stores one stem node every four depth. Each in turn trades size in the trie node and leaf columns for work on rebuild. The state root is identical whichever is chosen, and every layout remains readable regardless of this setting.", DefaultValue = "Interleaved")]
    PbtGroupFormat TrieNodeLevels { get; set; }

    [ConfigItem(Description = "How the stem trie is tiled into stored blobs: `ClusteredFourLevel` stores 4-level tiles with every other depth holding its children's blobs, `SixLevel` stores 6-level tiles each in a blob of its own. The state root is identical either way, but the keys are not: a database is stamped with the tiling that wrote it and cannot be read under the other.", DefaultValue = "ClusteredFourLevel")]
    PbtTiling TrieNodeTiling { get; set; }

    [ConfigItem(Description = "Number of parallel workers folding a block's writes into the tree. 0 uses the processor count, 1 folds on the calling thread only. A batch too small to be worth splitting folds on the calling thread whatever this says.", DefaultValue = "0")]
    int RootFoldConcurrency { get; set; }
}
