// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public sealed class BlockRangeBloomFilter(
    BloomFilter inner,
    long startingBlockNumber,
    long endingBlockNumber) : RefCountingDisposable, IBloomFilter
{
    public long StartingBlockNumber => startingBlockNumber;
    public long EndingBlockNumber => endingBlockNumber;
    public bool MightContain(ulong key) => inner.MightContain(key);
    public void Add(ulong key) => inner.Add(key);
    public bool TryAcquire() => TryAcquireLease();
    protected override void CleanUp() => inner.Dispose();
}
