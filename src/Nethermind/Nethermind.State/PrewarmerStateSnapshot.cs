// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;

namespace Nethermind.State;

/// <summary>
/// Thread-safe snapshot of committed state that the main block processor
/// publishes after each tx. The prewarmer reads from this to see fresh
/// state instead of always using stale parent state.
/// </summary>
public sealed class PrewarmerStateSnapshot
{
    private readonly ConcurrentDictionary<AddressAsKey, Account> _accounts = new();
    private readonly ConcurrentDictionary<StorageCell, byte[]> _storage = new();

    public void CommitAccount(Address address, Account account) =>
        _accounts[address] = account;

    public void CommitStorage(in StorageCell cell, byte[] value) =>
        _storage[cell] = value;

    public bool TryGetAccount(Address address, out Account? account) =>
        _accounts.TryGetValue(address, out account);

    public bool TryGetStorage(in StorageCell cell, out byte[]? value) =>
        _storage.TryGetValue(cell, out value);

    public ConcurrentDictionary<AddressAsKey, Account> Accounts => _accounts;
    public ConcurrentDictionary<StorageCell, byte[]> Storage => _storage;

    public void Clear()
    {
        _accounts.Clear();
        _storage.Clear();
    }
}
