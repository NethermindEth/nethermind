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
    public bool DisableHintSetWarmup { get; set; } = false;
    public bool DisableOutOfScopeWarmup { get; set; } = false;
    public FlatLayout Layout { get; set; } = FlatLayout.Flat;
    public int CompactSize { get; set; } = 32;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public int MaxReorgDepth { get; set; } = 256;
    public int MidCompactSize { get; set; } = 4;
    public int MinReorgDepth { get; set; } = 128;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public long BlockCacheSizeBudget { get; set; } = 1.GiB();
    public long TrieCacheMemoryBudget { get; set; } = 512.MiB();
}
