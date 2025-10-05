// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.FlatCache;

public interface IBigCache
{
    long CurrentBlockNumber { get; }
    long SnapshotCount { get; }
    bool TryGetValue(Address address, out Account? acc);
    IStorageReader GetStorageReader(Address address);

    public interface IStorageReader
    {
        bool TryGetValue(in UInt256 index, out byte[]? value);
    }

    void Subtract(StateId firstKey, Snapshot snapshot);
    void Add(StateId pickedSnapshot, Snapshot pickedState);
}
