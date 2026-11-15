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
/// A response can have slots of many accounts with few slots each. One buffer for the response, not one for each
/// account, keeps the number of pool rentals small.
/// </remarks>
/// <param name="accountCapacity">The number of accounts that the response can have.</param>
public sealed class EncodedStorageRanges(int accountCapacity) : IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>
{
    private readonly EncodedSnapEntries<PathWithStorageSlot> _buffer = new(PathWithStorageCollector.DecodeEntry);
    private readonly ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> _accounts = new(accountCapacity);

    /// <summary>Starts to collect the slots of the next account into the buffer of the response.</summary>
    /// <remarks>Collect all slots of an account and <see cref="Add"/> them before the next account starts.</remarks>
    public PathWithStorageCollector StartAccount() => new(new EncodedSnapEntries<PathWithStorageSlot>(PathWithStorageCollector.DecodeEntry, _buffer));

    /// <summary>Adds the slots of an account to the response.</summary>
    public void Add(IOwnedReadOnlyList<PathWithStorageSlot> slots) => _accounts.Add(slots);

    public int Count => _accounts.Count;

    public IOwnedReadOnlyList<PathWithStorageSlot> this[int index] => _accounts[index];

    public ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> AsSpan() => _accounts.AsSpan();

    public IEnumerator<IOwnedReadOnlyList<PathWithStorageSlot>> GetEnumerator() => _accounts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <remarks>Also releases the memory of the slot lists.</remarks>
    public void Dispose()
    {
        _accounts.Dispose();
        _buffer.Dispose();
    }
}
