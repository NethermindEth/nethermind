// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat;

public interface IBloomFilterManager
{
    ArrayPoolList<IBloomFilter> GetBloomFiltersForRange(long startingBlockNumber, long endingBlockNumber);
    void AddEntries(Snapshot snapshot);
}
