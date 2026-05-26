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
    public int MaxInMemoryBaseSnapshotCount { get; set; } = 128 + 32;
    public int MaxReorgDepth { get; set; } = 256;
    public int MinCompactSize { get; set; } = 2;
    public int MinReorgDepth { get; set; } = 128;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public long BlockCacheSizeBudget { get; set; } = 1.GiB;
    public long TrieCacheMemoryBudget { get; set; } = 512.MiB;
    public bool EnableLongFinality { get; set; } = false;
    public int LongFinalityReorgDepth { get; set; } = 90000;
    public string PersistedSnapshotPath { get; set; } = "snapshots";
    public long ArenaFileSizeBytes { get; set; } = 1L * 1024 * 1024 * 1024;
    public long PersistedSnapshotArenaPageCacheBytes { get; set; } = 8L * 1024 * 1024 * 1024;
    public bool PersistedSnapshotFadviseOnPageEviction { get; set; } = false;
    public bool PersistedSnapshotPunchHoleOnReclaim { get; set; } = true;
    public int PersistedSnapshotMaxCompactSize { get; set; } = 1024 * 8;
    public bool ValidatePersistedSnapshot { get; set; } = false;
    public double PersistedSnapshotBloomBitsPerKey { get; set; } = 14.0;
    public long PersistedSnapshotMaxCompactedSourceBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}
