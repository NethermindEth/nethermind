// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State;

internal ref struct AccountReadBatch(int capacity)
{
    internal const int MinReadSize = 8;

    private ArrayPoolListRef<Address> _addresses = new(0);
    private ArrayPoolListRef<int> _positions = new(0);
    private ArrayPoolListRef<HashedKey<Address>> _keys = new(0);

    internal readonly int Count => _addresses.Count;
    internal readonly ReadOnlySpan<Address> Addresses => _addresses.AsSpan();
    internal readonly ReadOnlySpan<int> Positions => _positions.AsSpan();
    internal readonly ReadOnlySpan<HashedKey<Address>> Keys => _keys.AsSpan();

    internal void Add(Address address)
    {
        if (_addresses.Capacity == 0) _addresses = new(capacity);
        _addresses.Add(address);
    }

    internal void Add(Address address, int position)
    {
        if (_positions.Capacity == 0) _positions = new(capacity);
        Add(address);
        _positions.Add(position);
    }

    internal void Add(in HashedKey<Address> key, int position)
    {
        if (_keys.Capacity == 0) _keys = new(capacity);
        Add(key.Key, position);
        _keys.Add(key);
    }

    public void Dispose()
    {
        _keys.Dispose();
        _positions.Dispose();
        _addresses.Dispose();
    }
}
