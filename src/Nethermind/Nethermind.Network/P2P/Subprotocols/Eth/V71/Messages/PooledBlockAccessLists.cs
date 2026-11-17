// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

/// <summary>Block access list entries as a <see cref="BlockAccessListsMessage"/> carries them, in one pooled buffer.</summary>
/// <remarks>
/// The serializer writes <see cref="Encoded"/> as it is, so serving copies no list into an array of its own.
/// Reading an entry copies it to an array.
/// </remarks>
/// <param name="buffer">The encoded entries, which this list then owns; an unavailable list is the empty string.</param>
/// <param name="entries">Where each entry is in <paramref name="buffer"/>.</param>
internal sealed class PooledBlockAccessLists(ArrayPoolList<byte> buffer, Range[] entries) : IOwnedReadOnlyList<byte[]?>
{
    private ArrayPoolList<byte>? _buffer = buffer;
    private byte[]?[]? _copied;

    public int Count => entries.Length;

    /// <summary>The encoded entries, one after another, valid until disposal.</summary>
    public ReadOnlySpan<byte> Encoded => (_buffer ?? throw new ObjectDisposedException(nameof(PooledBlockAccessLists))).AsSpan();

    public byte[]? this[int index] => AsSpan()[index];

    public ReadOnlySpan<byte[]?> AsSpan()
    {
        if (_copied is null)
        {
            ReadOnlySpan<byte> encoded = Encoded;
            _copied = new byte[]?[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                ReadOnlySpan<byte> entry = encoded[entries[i]];
                _copied[i] = entry is [Rlp.EmptyByteArrayByte] ? null : entry.ToArray();
            }
        }

        return _copied;
    }

    public IEnumerator<byte[]?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}
