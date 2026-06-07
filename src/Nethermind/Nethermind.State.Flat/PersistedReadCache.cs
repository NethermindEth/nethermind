// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

public sealed class PersistedReadCache
{
    private readonly SeqlockCache<AddressAsKey, Account> _accounts = new();
    private readonly SeqlockCache<StorageCell, byte[]> _slots = new();

    public bool TryGetAccount(Address address, out Account? account)
    {
        AddressAsKey key = address;
        return _accounts.TryGetValue(in key, out account);
    }

    public void SetAccount(Address address, Account? account)
    {
        AddressAsKey key = address;
        _accounts.Set(in key, account);
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[]? value)
    {
        StorageCell key = new(address, in index);
        return _slots.TryGetValue(in key, out value);
    }

    public void SetSlot(Address address, in UInt256 index, byte[]? value)
    {
        StorageCell key = new(address, in index);
        _slots.Set(in key, value);
    }

    public void Clear()
    {
        _accounts.Clear();
        _slots.Clear();
    }
}
