// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class NewPooledTransactionHashesMessage72(
    IOwnedReadOnlyList<byte> types,
    IOwnedReadOnlyList<int> sizes,
    IOwnedReadOnlyList<Hash256> hashes,
    byte[] cellMask) : P2PMessage
{
    public const int MaxCount = 2048;

    public override int PacketType => Eth72MessageCode.NewPooledTransactionHashes;
    public override string Protocol => "eth";

    public NewPooledTransactionHashesMessage72(byte[] types, int[] sizes, Hash256[] hashes, byte[] cellMask)
        : this(new ArrayOwnedReadOnlyList<byte>(types), new ArrayOwnedReadOnlyList<int>(sizes), new ArrayOwnedReadOnlyList<Hash256>(hashes), cellMask)
    {
    }

    public OwnedList<byte> Types { get; } = new(types);
    public OwnedList<int> Sizes { get; } = new(sizes);
    public OwnedList<Hash256> Hashes { get; } = new(hashes);
    public byte[] CellMask { get; } = cellMask;

    public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage72)}({Hashes.Count})";

    public override void Dispose()
    {
        base.Dispose();
        Types.Dispose();
        Sizes.Dispose();
        Hashes.Dispose();
    }

    public sealed class OwnedList<T>(IOwnedReadOnlyList<T> list) : IOwnedReadOnlyList<T>
    {
        public int Count => list.Count;
        public int Length => list.Count;

        public T this[int index] => list[index];

        public ReadOnlySpan<T> AsSpan() => list.AsSpan();

        public IEnumerator<T> GetEnumerator() => list.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() => list.Dispose();
    }

    private sealed class ArrayOwnedReadOnlyList<T>(T[] items) : IOwnedReadOnlyList<T>
    {
        public int Count => items.Length;

        public T this[int index] => items[index];

        public ReadOnlySpan<T> AsSpan() => items;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < items.Length; i++)
            {
                yield return items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
        }
    }
}
