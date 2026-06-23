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
    public bool EnableLongFinality { get; set; } = false;
    public int LongFinalityMaxReorgDepth { get; set; } = 90000;
    public int MaxInMemoryBaseSnapshotCount { get; set; } = 128;
    public long ArenaFileSizeBytes { get; set; } = 1.GiB;
    public long PersistedSnapshotDedicatedArenaThresholdBytes { get; set; } = 1.GiB;
    public bool PersistedSnapshotPunchHoleOnReclaim { get; set; } = true;
    public int PersistedSnapshotMaxCompactSize { get; set; } = 1024 * 1024;
    public bool ValidatePersistedSnapshot { get; set; } = false;
    public double PersistedSnapshotBloomBitsPerKey { get; set; } = 14.0;
}
