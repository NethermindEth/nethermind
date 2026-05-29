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
    public bool SparseTrieAuthoritativeStorage { get; set; } = true;
    public bool SparseTrieShadowStorageCompare { get; set; } = false;
    // Default to effectively-unbounded retention until we are confident Prune handles every
    // edge case (it currently triggers retry-loop failures on some realblocks blocks â€” see
    // the in-flight investigation). Set to lower values to actually prune.
    public int SparseTrieMaxHotAccounts { get; set; } = int.MaxValue;
    public int SparseTrieMaxHotSlots { get; set; } = int.MaxValue;
    public SparseTrieWarmerVariant SparseTrieWarmer { get; set; } = SparseTrieWarmerVariant.Legacy;
}
