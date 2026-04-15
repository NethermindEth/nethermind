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
/// <see cref="CumulativeSizeStats.CodeBytesTotal"/> and
/// <see cref="CumulativeSizeStats.SlotCountHistogram"/> updates to run.
/// A snapshot loaded without them must be treated as "no baseline" — the plugin
/// falls back to a fresh scan rather than applying diffs against an unknown state.
/// </para>
/// </summary>
public readonly record struct StateCompositionSnapshot(
    CumulativeSizeStats Stats,
    long BlockNumber,
    Hash256 StateRoot,
    int DiffsSinceBaseline,
    long ScanBlockNumber,
    CumulativeDepthStats? DepthStats = null,
    Dictionary<ValueHash256, long>? SlotCountByAddress = null,
    Dictionary<ValueHash256, int>? CodeHashRefcounts = null,
    Dictionary<ValueHash256, int>? CodeHashSizes = null);
