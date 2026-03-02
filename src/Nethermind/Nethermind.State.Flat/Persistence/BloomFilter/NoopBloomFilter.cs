// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Persistence.BloomFilter;

/// <summary>
/// A bloom filter that always returns true for <see cref="MightContain"/>, effectively disabling bloom-based skipping.
/// Used when no real bloom filter is available for a segment.
/// </summary>
public sealed class NoopBloomFilter : IBloomFilter
{
    public static readonly NoopBloomFilter Instance = new();
    public long StartingBlockNumber => 0;
    public long EndingBlockNumber => long.MaxValue;
    public bool MightContain(ulong key) => true;
    public void Add(ulong key) { }
}
