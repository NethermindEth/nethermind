// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Per-contract storage trie statistics.
/// Contains per-depth breakdown (Levels[16]), summary aggregate, and owner hash.
/// </summary>
public readonly record struct TopContractEntry
{
    /// <summary>Account hash (keccak256 of address).</summary>
    public ValueHash256 Owner { get; init; }

    public ValueHash256 StorageRoot { get; init; }
    public int MaxDepth { get; init; }
    public long TotalNodes { get; init; }
    public long ValueNodes { get; init; }
    public long TotalSize { get; init; }

    /// <summary>Per-depth node breakdown.</summary>
    public ImmutableArray<TrieLevelStat> Levels { get; init; }

    /// <summary>Aggregate across all depths.</summary>
    public TrieLevelStat Summary { get; init; }
}
