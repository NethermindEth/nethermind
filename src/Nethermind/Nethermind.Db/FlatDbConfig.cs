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
    // Match the IFlatDbConfig DefaultValue documentation. The previous int.MaxValue defaults
    // silently disabled LFU pruning despite the ConfigItem docs advertising 50000/200000 â€”
    // operators and benchmarks ran without actually exercising Prune. With F1's "never blind
    // the root" fix in place, Prune is safe to default on. Set explicitly to int.MaxValue to
    // disable pruning (cross-block trie keeps growing); set lower for tighter memory budgets.
    public int SparseTrieMaxHotAccounts { get; set; } = 50_000;
    public int SparseTrieMaxHotSlots { get; set; } = 200_000;
    // Static default stays at Legacy â€” the DI wiring in FlatWorldStateModule overrides this
    // to None whenever UseSparseRootComputation=true, because the Legacy warmer walks Patricia
    // which is duplicate work once the sparse trie is authoritative. The override is in DI
    // rather than here so non-sparse deployments keep the Patricia warmer without extra
    // config gymnastics.
    public SparseTrieWarmerVariant SparseTrieWarmer { get; set; } = SparseTrieWarmerVariant.Legacy;
}
