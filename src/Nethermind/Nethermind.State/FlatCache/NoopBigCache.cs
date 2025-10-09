// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.FlatCache;

public class NoopBigCache: IBigCache
{
    public StateId CurrentState { get; }
    public IBigCache.IBigCacheReader CreateReader()
    {

        throw new System.NotImplementedException();
    }

    public void Add(StateId pickedSnapshot, Snapshot pickedState)
    {
    }

    public class NoopbigCacheReader : IBigCache.IBigCacheReader
    {
        public void Dispose()
        {
        }

        public StateId CurrentState { get; }
        public bool TryGetValue(Address address, out Account? acc)
        {
            acc = null;
            return false;
        }

        public IBigCache.IStorageReader GetStorageReader(Address address)
        {
            return new NoopBigCacheStorageReader();
        }
    }

    public class NoopBigCacheStorageReader : IBigCache.IStorageReader
    {
        public bool TryGetValue(in UInt256 index, out byte[]? value)
        {
            value = null;
            return false;
        }
    }
}
