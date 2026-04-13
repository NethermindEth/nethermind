// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Persisted snapshot of incremental state composition stats at a specific block.
/// Stored in the stateComposition RocksDB database for warm restart and historical queries.
/// </summary>
public readonly record struct StateCompositionSnapshot(
    CumulativeSizeStats Stats,
    long BlockNumber,
    Hash256 StateRoot,
    int DiffsSinceBaseline,
    long ScanBlockNumber,
    CumulativeDepthStats? DepthStats = null);
