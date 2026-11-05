// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Insertion-ordered view over <see cref="GeneratedBlockAccessList"/>'s accounts. <c>foreach</c>
/// uses the dictionary's struct enumerator (zero allocations); the explicit
/// <see cref="IEnumerable{T}"/> implementation is provided for serializers and other API
/// consumers that erase the static type.
/// </summary>
public readonly struct GeneratedAccountChangesView : IEnumerable<GeneratedAccountChanges>
{
    private readonly Dictionary<AddressAsKey, GeneratedAccountChanges> _accounts;

    internal GeneratedAccountChangesView(Dictionary<AddressAsKey, GeneratedAccountChanges> accounts) => _accounts = accounts;

    public int Count => _accounts.Count;

    public Dictionary<AddressAsKey, GeneratedAccountChanges>.ValueCollection.Enumerator GetEnumerator()
        => _accounts.Values.GetEnumerator();

    IEnumerator<GeneratedAccountChanges> IEnumerable<GeneratedAccountChanges>.GetEnumerator()
        => _accounts.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _accounts.Values.GetEnumerator();
}
