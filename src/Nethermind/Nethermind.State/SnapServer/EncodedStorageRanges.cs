// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.State.Snap;

namespace Nethermind.State.SnapServer;

/// <summary>The slot lists of a storage range response, one for each account, with all entries in one pooled buffer.</summary>
/// <remarks>
/// A response can have slots of many accounts with few slots each. One buffer for the response, and the place of each
/// account in it, keep the work for each account small: the serializer copies <see cref="GetEncoded"/> as it is, and
/// the slot lists of the accounts are made only when the entries are read.
/// </remarks>
/// <param name="accountCapacity">The number of accounts that the response can have.</param>
public sealed class EncodedStorageRanges(int accountCapacity) : IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>
{
    private readonly EncodedSnapEntries<PathWithStorageSlot> _buffer = new(PathWithStorageCollector.DecodeEntry);
    private readonly ArrayPoolList<AccountEntries> _accounts = [with(accountCapacity)];
    private IOwnedReadOnlyList<PathWithStorageSlot>[]? _slotLists;
    private int _accountStart;
    private int _accountFirstEntry;

    /// <summary>Collects the slots of the current account into the buffer of the response.</summary>
    public PathWithStorageCollector Collector => field ??= new(_buffer);

    /// <summary>Adds the slots collected since the previous account to the response as the slots of one account.</summary>
    /// <returns><see langword="false"/> when no slot was collected, so no account is added.</returns>
    public bool TryCompleteAccount()
    {
        int count = _buffer.Count - _accountFirstEntry;
        if (count == 0) return false;

        int end = _buffer.Encoded.Length;
        _accounts.Add(new AccountEntries(_accountStart, end - _accountStart, count));
        _accountStart = end;
        _accountFirstEntry = _buffer.Count;
        _slotLists = null;
        return true;
    }

    /// <summary>The encoded slot entries of the account at <paramref name="index"/>, without the prefix of the list.</summary>
    public ReadOnlySpan<byte> GetEncoded(int index)
    {
        AccountEntries account = _accounts[index];
        return _buffer.Encoded.Slice(account.Start, account.Length);
    }

    public int Count => _accounts.Count;

    public IOwnedReadOnlyList<PathWithStorageSlot> this[int index] => AsSpan()[index];

    public ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> AsSpan() => _slotLists ??= CreateSlotLists();

    public IEnumerator<IOwnedReadOnlyList<PathWithStorageSlot>> GetEnumerator() => ((IEnumerable<IOwnedReadOnlyList<PathWithStorageSlot>>)(_slotLists ??= CreateSlotLists())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IOwnedReadOnlyList<PathWithStorageSlot>[] CreateSlotLists()
    {
        IOwnedReadOnlyList<PathWithStorageSlot>[] slotLists = new IOwnedReadOnlyList<PathWithStorageSlot>[_accounts.Count];
        for (int i = 0; i < slotLists.Length; i++)
        {
            AccountEntries account = _accounts[i];
            slotLists[i] = new EncodedSnapEntries<PathWithStorageSlot>(PathWithStorageCollector.DecodeEntry, _buffer, account.Start, account.Length, account.Count);
        }

        return slotLists;
    }

    /// <remarks>Also releases the memory of the slot lists, which then read as empty.</remarks>
    public void Dispose()
    {
        if (_slotLists is not null)
        {
            foreach (IOwnedReadOnlyList<PathWithStorageSlot> slots in _slotLists) slots.Dispose();
            _slotLists = null;
        }

        _accounts.Dispose();
        _buffer.Dispose();
    }

    private readonly record struct AccountEntries(int Start, int Length, int Count);
}
