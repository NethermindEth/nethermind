// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

public class PbtConfig : IPbtConfig
{
    public bool Enabled { get; set; }
    public int CompactSize { get; set; } = 32;
    public long CompactionOffset { get; set; } = -1;
    public int MinReorgDepth { get; set; } = 128;
    public int MaxReorgDepth { get; set; } = 256;
    public bool ImportFromPreimageFlat { get; set; }
    public int ImportStorageReadConcurrency { get; set; }
    public int ImportWindowSize { get; set; }
    public bool ScanTree { get; set; }
    public int ScanTreeConcurrency { get; set; }
    public PbtGroupFormat TrieNodeLevels { get; set; } = PbtGroupFormat.Interleaved;
    public int RootFoldConcurrency { get; set; }
}
