// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Db;

public class FlatDbConfig : IFlatDbConfig
{
    public bool Enabled { get; set; } = false;
    public bool ImportFromPruningTrieState { get; set; } = false;
    public int PruningBoundary { get; set; } = 128;
    public int CompactSize { get; set; } = 32;
    public int MidCompactSize { get; set; } = 4;
    public int MaxInFlightCompactJob { get; set; } = 32;
    public bool ReadWithTrie { get; set; } = false;
    public bool VerifyWithTrie { get; set; } = false;
    public bool InlineCompaction { get; set; } = false;

    // 1 GB is enough for 10% dirty load. 512 MB is pretty good at around 20%. Without it, then the diff layers on its own have around 35% dirty load.
    public long TrieCacheMemoryTarget { get; set; } = 512.MiB();
    public FlatLayout Layout { get; set; } = FlatLayout.Flat;
    public long BlockCacheSizeBudget { get; set; } = 1.GiB();
    public int MaxPruningBoundary { get; set; } = 1024;
    public int TrieWarmerWorkerCount { get; set; } = -1;
    public bool EnablePreimageRecording { get; set; } = false;
}
