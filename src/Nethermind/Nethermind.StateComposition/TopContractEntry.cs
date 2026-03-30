// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Entry for Top-N contract rankings — matches Geth's inspect-trie heap entries.
/// Tracks per-contract storage trie statistics.
/// </summary>
public readonly record struct TopContractEntry
{
    public ValueHash256 StorageRoot { get; init; }
    public int MaxDepth { get; init; }
    public long TotalNodes { get; init; }
    public long StorageSlots { get; init; }
    public long ByteSize { get; init; }
}
