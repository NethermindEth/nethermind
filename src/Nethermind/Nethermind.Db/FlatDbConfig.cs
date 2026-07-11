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
    public ulong CompactSize { get; set; } = 32;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public ulong MaxReorgDepth { get; set; } = 256;
    public ulong MinReorgDepth { get; set; } = 128;
    public long PersistenceWriteBufferFloor { get; set; } = 16.MiB;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public int WarmReadConcurrency { get; set; } = -1;
    public ulong BlockCacheSizeBudget { get; set; } = 1UL.GiB;
    public long CompactionOffset { get; set; } = -1;
    public ulong TrieCacheMemoryBudget { get; set; } = 512UL.MiB;
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
    public bool UseSparseRootComputation { get; set; } = false;
    public bool SparseTrieVerificationMode { get; set; } = false;
    public bool SparseTrieSkipPatricia { get; set; } = false;
    public bool SparseTrieForceAuthoritative { get; set; } = false;
    public bool SparseTrieParallelRoot { get; set; } = false;
    public bool SparseTrieAuthoritativeStorage { get; set; } = true;
    public bool SparseTrieShadowStorageCompare { get; set; } = false;
    // Prune defaults match the IFlatDbConfig docs (now both int.MaxValue = disabled).
    // EXPB 26637010048 showed that on realblocks (1000 consecutive recent blocks) the LFU
    // hit rate is too low for Prune to pay for itself â€” p95 regresses ~50 ms when enabled.
    // F1's "never blind the root" fix makes Prune SAFE; this default just keeps it OFF
    // until either (a) M5's sparse-aware prefetcher closes the proof-read overhead, or
    // (b) a workload with stronger hot-account locality is targeted. Set lower values
    // explicitly to opt in.
    public int SparseTrieMaxHotAccounts { get; set; } = int.MaxValue;
    public int SparseTrieMaxHotSlots { get; set; } = int.MaxValue;
    // Triggered (not per-block) memory bound for the preserved trie: prune only fires on a
    // commit where retained storage-trie count exceeds this. int.MaxValue = no bound, matching
    // historical behaviour. See IFlatDbConfig for why per-block evict-to-cap was abandoned
    // (7.5x regression on realblocks â€” it discards the working set).
    public int SparseTrieMaxRetainedStorageTries { get; set; } = int.MaxValue;
    // Default Legacy. NOTE: there is intentionally NO DI override forcing this to None under
    // sparse mode â€” an earlier attempt to do that (treating the Legacy warmer as redundant once
    // sparse is authoritative) regressed realblocks badly (MIN 0.5ms -> 35ms) because the Legacy
    // Patricia warmer also primes the OS/RocksDB page cache that the sparse proof reads hit. It
    // was reverted; FlatWorldStateModule only substitutes NoopTrieWarmer when the warmer is
    // explicitly None or TrieWarmerWorkerCount == 0. The warmer stays Legacy in sparse mode until
    // a sparse-native prewarmer (feeds proofs back into the trie) replaces it.
    public SparseTrieWarmerVariant SparseTrieWarmer { get; set; } = SparseTrieWarmerVariant.Legacy;
}
