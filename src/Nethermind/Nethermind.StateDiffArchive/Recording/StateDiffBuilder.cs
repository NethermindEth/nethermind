// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.StateDiffArchive.Data;

namespace Nethermind.StateDiffArchive.Recording;

/// <summary>
/// Accumulates the account/storage/code writes teed from one block's world-state write batch, grouped by
/// address, and seals them into a <see cref="StateDiffRecord"/> on commit.
/// </summary>
internal sealed class StateDiffBuilder
{
    private readonly Dictionary<Address, Entry> _accounts = [];
    private readonly List<CodeDiff> _codes = [];

    public void SetAccount(Address address, Account? account)
    {
        Entry entry = GetOrAdd(address);
        entry.Change = account is null ? AccountChangeKind.Deleted : AccountChangeKind.Set;
        entry.Account = account;
    }

    public void ClearStorage(Address address) => GetOrAdd(address).StorageCleared = true;

    public void SetSlot(Address address, in UInt256 index, byte[] value)
        => (GetOrAdd(address).Slots ??= []).Add(new SlotDiff(index, value));

    public void AddCode(in ValueHash256 codeHash, byte[] code) => _codes.Add(new CodeDiff(codeHash, code));

    public StateDiffRecord Build(ulong blockNumber, Hash256 stateRoot)
    {
        List<AccountDiff> accounts = new(_accounts.Count);
        foreach ((Address address, Entry entry) in _accounts)
        {
            accounts.Add(new AccountDiff(
                address,
                entry.Change,
                entry.Account,
                entry.StorageCleared,
                (IReadOnlyList<SlotDiff>?)entry.Slots ?? []));
        }

        return new StateDiffRecord(StateDiffRecord.CurrentVersion, blockNumber, stateRoot, accounts, _codes.ToArray());
    }

    public void Reset()
    {
        _accounts.Clear();
        _codes.Clear();
    }

    private Entry GetOrAdd(Address address)
    {
        if (!_accounts.TryGetValue(address, out Entry? entry))
        {
            entry = new Entry();
            _accounts[address] = entry;
        }
        return entry;
    }

    private sealed class Entry
    {
        public AccountChangeKind Change = AccountChangeKind.None;
        public Account? Account;
        public bool StorageCleared;
        public List<SlotDiff>? Slots;
    }
}
