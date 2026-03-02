// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat;

public sealed class NoopBloomFilterManager : IBloomFilterManager
{
    public static readonly NoopBloomFilterManager Instance = new();

    public ArrayPoolList<IBloomFilter> GetBloomFiltersForRange(long startingBlockNumber, long endingBlockNumber) =>
        new(0);

    public void AddEntries(Snapshot snapshot) { }
}
