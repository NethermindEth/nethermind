// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Persisted snapshot of incremental state composition stats at a specific block.
/// Stored in the stateComposition RocksDB database for warm restart and historical queries.
/// <para>
/// The tracker maps (<see cref="SlotCountByAddress"/>, <see cref="CodeHashRefcounts"/>,
/// <see cref="CodeHashSizes"/>) are required for the incremental
/// <see cref="CumulativeTrieStats.CodeBytesTotal"/> and
/// <see cref="CumulativeTrieStats.SlotCountHistogram"/> updates to run. The decoder
/// always materializes them — even if empty — so the holder can take ownership without
/// nullable fallbacks. <see cref="DepthStats"/> uses its own <see cref="CumulativeDepthStats.IsSeeded"/>
/// flag as the "depth distribution available" gate.
/// </para>
/// </summary>
public readonly record struct StateCompositionSnapshot(
    CumulativeTrieStats Stats,
    long BlockNumber,
    Hash256 StateRoot,
    int DiffsSinceBaseline,
    long ScanBlockNumber,
    CumulativeDepthStats DepthStats,
    Dictionary<ValueHash256, long> SlotCountByAddress,
    Dictionary<ValueHash256, int> CodeHashRefcounts,
    Dictionary<ValueHash256, int> CodeHashSizes);
