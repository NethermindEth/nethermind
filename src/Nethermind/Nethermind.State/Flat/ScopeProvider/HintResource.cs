// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.ScopeProvider;

public record HintResource(
    ConcurrentDictionary<AddressAsKey, Account> Accounts,
    ConcurrentDictionary<(AddressAsKey, UInt256), byte[]> Slots
)
{
    public void Clear()
    {
        Accounts.Clear();
        Slots.Clear();
    }

    public bool TryGetAccount(AddressAsKey address, out Account acc)
    {
        return Accounts.TryGetValue(address, out acc);
    }

    public bool TryGetSlot(AddressAsKey address, in UInt256 index, out byte[] value)
    {
        return Slots.TryGetValue((address, index), out value);
    }

    public bool TryAddAccount(Address address, Account account)
    {
        return Accounts.TryAdd(address, account);
    }

    public bool TryAddSlot(AddressAsKey address, UInt256 index, byte[] value)
    {
        return Slots.TryAdd((address, index), value);
    }
}
