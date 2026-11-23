// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Test;

/// <summary>A memory database that records the keys of each prefetch.</summary>
public sealed class PrefetchingTestDb : TestMemDb, IReadPrefetcher
{
    /// <summary>The keys of each prefetch, one key per entry.</summary>
    public List<byte[][]> Prefetches { get; } = [];

    /// <summary>The keys that <see cref="IsCached"/> reports as cached.</summary>
    public HashSet<byte[]> CachedKeys { get; } = [with(Bytes.EqualityComparer)];

    public void Prefetch(ReadOnlySpan<byte> keys, int keyLength)
    {
        byte[][] split = new byte[keys.Length / keyLength][];
        for (int i = 0; i < split.Length; i++) split[i] = keys.Slice(i * keyLength, keyLength).ToArray();
        Prefetches.Add(split);
    }

    public bool IsCached(ReadOnlySpan<byte> key) => CachedKeys.Contains(key.ToArray());
}
