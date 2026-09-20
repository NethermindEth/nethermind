// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;

internal sealed class AnnouncementHashes : IOwnedReadOnlyList<Hash256>
{
    internal readonly ArrayPoolList<ValueHash256> Values = new(0);
    private readonly ArrayPoolList<Hash256> _hashes = new(0);

    public int Count => Values.Count;
    internal int Capacity => Math.Max(Values.Capacity, _hashes.Capacity);

    public Hash256 this[int index]
    {
        get
        {
            ValueHash256 value = Values[index];
            while (_hashes.Count < Count) _hashes.Add(null!);
            return _hashes[index] ??= new Hash256(value);
        }
    }

    public ReadOnlySpan<Hash256> AsSpan()
    {
        for (int i = 0; i < Count; i++) _ = this[i];
        return _hashes.AsSpan();
    }

    public IEnumerator<Hash256> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Clear()
    {
        Values.Clear();
        _hashes.Clear();
    }

    public void Dispose()
    {
        Values.Dispose();
        _hashes.Dispose();
    }
}
