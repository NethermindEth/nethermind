// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db;

public class FlatDbConfig : IFlatDbConfig
{
    public bool Enabled { get; set; } = false;
    public bool EnablePreimageRecording { get; set; } = false;
    public bool FlatNodeStorage { get; set; } = false;
    public bool FlatNodeStorageDebugChecks { get; set; } = false;
    public bool ImportFromPruningTrieState { get; set; } = false;
    public bool InlineCompaction { get; set; } = false;
    public bool RegenerateCompactionOffset { get; set; } = false;
    public bool VerifyWithTrie { get; set; } = false;
    public FlatLayout Layout { get; set; } = FlatLayout.Flat;
    public ulong CompactSize { get; set; } = 32;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public ulong MaxInMemorySnapshotBytes { get; set; } = 0;
    public ulong MaxReorgDepth { get; set; } = 256;
    public ulong MinReorgDepth { get; set; } = 128;
    public long GcPaceIntervalMs { get; set; } = 0;
    public long GcPaceGen0IntervalMs { get; set; } = 0;
    public long GcPaceGen2IntervalMs { get; set; } = 0;
    public long GcPaceWarmupSeconds { get; set; } = 0;
    public long PersistenceWriteBufferFloor { get; set; } = 16.MiB;
    public bool PersistViaSstIngestion { get; set; } = false;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public int WarmReadConcurrency { get; set; } = -1;
    public ulong BlockCacheSizeBudget { get; set; } = 1UL.GiB;
    public long CompactionOffset { get; set; } = -1;
    public ulong TrieCacheMemoryBudget { get; set; } = 512UL.MiB;
    public int TrieNodeRlpCacheCapacity { get; set; } = 1 << 18;
    public bool EnableLongFinality { get; set; } = true;
    public ulong LongFinalityMaxReorgDepth { get; set; } = 90000;
    public int MaxInMemoryBaseSnapshotCount { get; set; } = 160;
    public long ArenaFileSizeBytes { get; set; } = 1.GiB;
    public long PersistedSnapshotDedicatedArenaThresholdBytes { get; set; } = 1.GiB;
    public long PersistedSnapshotArenaPageCacheBytes { get; set; } = 4.GiB;
    public bool PersistedSnapshotPunchHoleOnReclaim { get; set; } = true;
    public ulong PersistedSnapshotMaxCompactSize { get; set; } = 1024 * 1024;
    public bool ValidatePersistedSnapshot { get; set; } = false;
    public double PersistedSnapshotBloomBitsPerKey { get; set; } = 14.0;
}
