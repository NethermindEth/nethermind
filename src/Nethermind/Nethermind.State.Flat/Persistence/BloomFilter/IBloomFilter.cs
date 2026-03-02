// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public interface IBloomFilter : IDisposable
{
    long StartingBlockNumber { get; }
    long EndingBlockNumber { get; }
    bool MightContain(ulong key);
    void Add(ulong key);
}
