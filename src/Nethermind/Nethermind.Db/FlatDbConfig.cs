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
    public bool VerifyWithTrie { get; set; } = false;
    public FlatLayout Layout { get; set; } = FlatLayout.Flat;
    public int CompactSize { get; set; } = 32;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public int MaxReorgDepth { get; set; } = 256;
    public int MinCompactSize { get; set; } = 2;
    public int MinReorgDepth { get; set; } = 128;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public long BlockCacheSizeBudget { get; set; } = 1.GiB;
    public long TrieCacheMemoryBudget { get; set; } = 512.MiB;
    public bool UseSparseRootComputation { get; set; } = false;
    public bool SparseTrieVerificationMode { get; set; } = false;
    public bool SparseTrieSkipPatricia { get; set; } = false;
    public bool SparseTrieForceAuthoritative { get; set; } = false;
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
    // Static default stays at Legacy â€” the DI wiring in FlatWorldStateModule overrides this
    // to None whenever UseSparseRootComputation=true, because the Legacy warmer walks Patricia
    // which is duplicate work once the sparse trie is authoritative. The override is in DI
    // rather than here so non-sparse deployments keep the Patricia warmer without extra
    // config gymnastics.
    public SparseTrieWarmerVariant SparseTrieWarmer { get; set; } = SparseTrieWarmerVariant.Legacy;
}
