// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db;

public class FlatDbConfig : IFlatDbConfig
{
    public bool Enabled { get; set; } = false;
    public bool EnablePreimageRecording { get; set; } = false;
    public bool ImportFromPruningTrieState { get; set; } = false;
    public bool InlineCompaction { get; set; } = false;
    public bool RegenerateCompactionOffset { get; set; } = false;
    public bool VerifyWithTrie { get; set; } = false;
    public FlatLayout Layout { get; set; } = FlatLayout.Flat;
    public int CompactSize { get; set; } = 32;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public int MaxReorgDepth { get; set; } = 256;
    public int MinReorgDepth { get; set; } = 128;
    public long PersistenceWriteBufferFloor { get; set; } = 16.MiB;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public int WarmReadConcurrency { get; set; } = -1;
    public long BlockCacheSizeBudget { get; set; } = 1.GiB;
    public long CompactionOffset { get; set; } = -1;
    public long TrieCacheMemoryBudget { get; set; } = 512.MiB;
}
